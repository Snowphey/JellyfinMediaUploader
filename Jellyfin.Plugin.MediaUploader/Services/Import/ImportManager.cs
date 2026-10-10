using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Un morceau d'un import et son état.</summary>
public sealed class ImportTrackState
{
    /// <summary>Gets le morceau.</summary>
    public required TrackSpec Spec { get; init; }

    /// <summary>Gets or sets l'identifiant du fichier correspondant dans le lot.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets l'état : "queued", "downloading", "done", "failed" ou "skipped".</summary>
    public string State { get; set; } = "queued";

    /// <summary>Gets or sets le détail (cause d'un échec).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets l'étape en cours : "waiting", "searching", "downloading", "converting", "tagging" ou "filing" (null hors téléchargement).</summary>
    public string? Phase { get; set; }

    /// <summary>Gets or sets l'avancement du téléchargement en pourcentage (étape "downloading").</summary>
    public int? Percent { get; set; }

    /// <summary>Gets or sets a value indicating whether le morceau est retenu pour le téléchargement.</summary>
    public bool Selected { get; set; } = true;
}

/// <summary>
/// Un import en cours : résolution du lien, choix des morceaux, téléchargements.
/// </summary>
public sealed class ImportJob
{
    /// <summary>Gets l'identifiant.</summary>
    public required string Id { get; init; }

    /// <summary>Gets le propriétaire (utilisateur ou clé d'API).</summary>
    public required string Owner { get; init; }

    /// <summary>Gets l'origine : "youtube", "spotify" ou "browse".</summary>
    public required string Source { get; init; }

    /// <summary>Gets or sets l'état : "resolving", "ready", "downloading", "done", "failed" ou "cancelled".</summary>
    public string State { get; set; } = "resolving";

    /// <summary>Gets or sets le message (erreur ou bilan).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets le titre de la liste.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets le genre de contenu.</summary>
    public string Kind { get; set; } = "playlist";

    /// <summary>Gets or sets la pochette de la liste.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets or sets le rangement : "source" ou "playlist".</summary>
    public string Layout { get; set; } = "source";

    /// <summary>Gets or sets a value indicating whether une liste de lecture Jellyfin est créée avec les morceaux (playlists seulement).</summary>
    public bool CreatePlaylist { get; set; }

    /// <summary>Gets or sets a value indicating whether cette liste est publique.</summary>
    public bool PlaylistPublic { get; set; }

    /// <summary>Gets or sets l'identifiant du lot créé au démarrage des téléchargements.</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets la date de création (UTC).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets la date de dernière activité (UTC).</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets a value indicating whether les téléchargements démarrent dès la résolution (sélection d'albums).</summary>
    public bool AutoStart { get; set; }

    /// <summary>Gets les morceaux (à manipuler sous <see cref="Sync"/>).</summary>
    public List<ImportTrackState> Tracks { get; } = new();

    /// <summary>Gets l'objet de verrouillage.</summary>
    public object Sync { get; } = new();

    /// <summary>Gets l'annulation de toutes les tâches du job.</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Gets a value indicating whether le job occupe encore des ressources.</summary>
    public bool Active => State is "resolving" or "ready" or "downloading";
}

/// <summary>
/// Orchestre les imports : résolution d'un lien ou d'albums, création du lot, téléchargements en parallèle limité.
/// Les fichiers téléchargés rejoignent un lot du flux habituel (aperçu, corrections, confirmation).
/// </summary>
public static class ImportManager
{
    /// <summary>Nombre maximal d'imports actifs par utilisateur.</summary>
    public const int MaxActiveJobsPerOwner = 3;

    private const int MaxBatchesPerOwner = 10;

    /// <summary>Gets les imports connus.</summary>
    public static ConcurrentDictionary<string, ImportJob> Jobs { get; } = new();

    /// <summary>
    /// Oublie les imports anciens (et arrête ceux qui traînent).
    /// </summary>
    public static void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, job) in Jobs)
        {
            var age = now - job.Updated;
            // Un import « prêt » que personne ne lance occupe l'une des places de l'utilisateur : il expire vite.
            var limit = job.State == "ready" ? TimeSpan.FromMinutes(30) : job.Active ? TimeSpan.FromHours(12) : TimeSpan.FromHours(3);
            if (age > limit)
            {
                job.Cts.Cancel();
                Jobs.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Nombre d'imports actifs d'un utilisateur.
    /// </summary>
    /// <param name="owner">Propriétaire.</param>
    /// <returns>Nombre.</returns>
    public static int ActiveCount(string owner) => Jobs.Values.Count(j => j.Owner == owner && j.Active);

    /// <summary>
    /// Lance la résolution (lecture du lien ou des albums) en arrière-plan.
    /// </summary>
    /// <param name="job">Import.</param>
    /// <param name="ctx">Outils et réglages.</param>
    /// <param name="resolve">Fonction qui produit le contenu.</param>
    /// <param name="logger">Logger.</param>
    public static void StartResolve(ImportJob job, ToolContext ctx, Func<CancellationToken, Task<Listing>> resolve, ILogger logger)
    {
        Jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                var listing = await resolve(job.Cts.Token).ConfigureAwait(false);
                lock (job.Sync)
                {
                    // Annulé (ou expiré) pendant la lecture du lien : on ne le ressuscite pas.
                    if (job.State != "resolving")
                    {
                        return;
                    }

                    job.Title = listing.Title;
                    job.Kind = listing.Kind;
                    job.CoverUrl = listing.CoverUrl;
                    job.Message = listing.Note;
                    job.Tracks.Clear();
                    job.Tracks.AddRange(listing.Tracks.Select(t => new ImportTrackState { Spec = t }));
                    job.State = "ready";
                    job.Updated = DateTime.UtcNow;
                }

                if (job.AutoStart)
                {
                    await BeginDownloadAsync(job, null, job.Layout, ctx, logger).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Finish(job, "cancelled", "Import annulé.");
            }
            catch (ImportException ex)
            {
                Finish(job, "failed", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaUploader: échec de la résolution d'un import");
                Finish(job, "failed", "Erreur inattendue (détails dans le journal de Jellyfin).");
            }
        });
    }

    /// <summary>
    /// Crée le lot pour les morceaux retenus et lance les téléchargements en arrière-plan.
    /// </summary>
    /// <param name="job">Import prêt.</param>
    /// <param name="selected">Identifiants de morceaux retenus (null = tous).</param>
    /// <param name="layout">"source" ou "playlist".</param>
    /// <param name="ctx">Outils et réglages.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Tâche terminée une fois les téléchargements lancés.</returns>
    public static Task BeginDownloadAsync(ImportJob job, IReadOnlyCollection<string>? selected, string layout, ToolContext ctx, ILogger logger)
    {
        Batch batch;
        lock (job.Sync)
        {
            if (job.State != "ready")
            {
                throw new ImportException("Cet import n'est pas prêt à démarrer.");
            }

            if (string.IsNullOrWhiteSpace(ctx.MusicRoot))
            {
                throw new ImportException("Le dossier musique n'est pas configuré (Tableau de bord > Extensions > Media Uploader).");
            }

            if (UploadStore.Batches.Values.Count(b => b.Owner == job.Owner) >= MaxBatchesPerOwner)
            {
                throw new ImportException("Trop de lots en attente : confirmez ou annulez les précédents.");
            }

            var wanted = selected?.ToHashSet(StringComparer.Ordinal);
            foreach (var t in job.Tracks)
            {
                t.Selected = wanted is null || wanted.Contains(t.Spec.Id);
            }

            var chosen = job.Tracks.Where(t => t.Selected).ToList();
            if (chosen.Count == 0)
            {
                throw new ImportException("Aucun morceau sélectionné.");
            }

            job.Layout = ImportNaming.NormalizeLayout(layout);
            batch = new Batch { Id = Guid.NewGuid().ToString("N"), Owner = job.Owner, Mode = "music" };
            lock (batch.Sync)
            {
                var number = 0;
                var order = new List<string>();
                foreach (var (t, index) in job.Tracks.Select((t, i) => (t, i + 1)))
                {
                    if (!t.Selected)
                    {
                        continue;
                    }

                    var meta = ImportNaming.Resolve(t.Spec, job.Title, job.Layout, index);
                    var id = "i" + number++;
                    t.ItemId = id;
                    order.Add(id);
                    batch.Items.Add(new BatchItem
                    {
                        Id = id,
                        ClientPath = ImportNaming.ClientPath(meta, t.Spec.Title, ctx.Extension),
                        Size = 0,
                        Ext = ctx.Extension,
                        Tags = new TagInfo(meta.AlbumArtist, meta.Album, t.Spec.Title),
                        ImportState = "queued"
                    });
                    if (job.Layout == "playlist")
                    {
                        batch.Overrides[id] = new ItemOverride { Artist = meta.AlbumArtist, Album = meta.Album };
                    }
                }

                // Seul un utilisateur Jellyfin (pas une clé d'API) peut posséder une liste de lecture.
                if (job.CreatePlaylist && job.Kind == "playlist" && Guid.TryParse(job.Owner, out var userId))
                {
                    batch.Playlist = new PlaylistRequest { Name = job.Title, UserId = userId, Public = job.PlaylistPublic, Order = order };
                }
            }

            UploadStore.Batches[batch.Id] = batch;
            job.BatchId = batch.Id;
            job.State = "downloading";
            job.Updated = DateTime.UtcNow;
        }

        _ = Task.Run(() => RunDownloadsAsync(job, batch, ctx, logger));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Arrête un import : les téléchargements en cours sont interrompus, les morceaux pas encore reçus quittent le lot.
    /// </summary>
    /// <param name="job">Import.</param>
    public static void Cancel(ImportJob job)
    {
        job.Cts.Cancel();
        lock (job.Sync)
        {
            foreach (var t in job.Tracks.Where(t => t.State is "queued" or "downloading"))
            {
                t.State = "skipped";
                t.Message = "Annulé.";
            }
        }

        if (job.BatchId is not null && UploadStore.Batches.TryGetValue(job.BatchId, out var batch))
        {
            lock (batch.Sync)
            {
                foreach (var item in batch.Items.Where(i => !i.Complete).ToList())
                {
                    batch.Items.Remove(item);
                    batch.Overrides.Remove(item.Id);
                    UploadStore.TryDelete(item.TempPath);
                }
            }

            batch.Invalidate();
        }

        Finish(job, "cancelled", "Import annulé.");
    }

    /// <summary>
    /// Arrête les imports qui alimentent un lot (annulation du lot par l'utilisateur).
    /// </summary>
    /// <param name="batchId">Identifiant du lot.</param>
    public static void CancelForBatch(string batchId)
    {
        foreach (var job in Jobs.Values.Where(j => j.BatchId == batchId && j.Active))
        {
            job.Cts.Cancel();
            Finish(job, "cancelled", "Lot annulé.");
        }
    }

    private static async Task RunDownloadsAsync(ImportJob job, Batch batch, ToolContext ctx, ILogger logger)
    {
        var ct = job.Cts.Token;
        using var gate = new SemaphoreSlim(Math.Clamp(ctx.Concurrency, 1, 4));
        var covers = new ConcurrentDictionary<string, byte[]>();
        List<ImportTrackState> chosen;
        lock (job.Sync)
        {
            chosen = job.Tracks.Where(t => t.Selected).ToList();
        }

        var tasks = chosen.Select(t => Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // Rythme humain, commun à tout le serveur : délai aléatoire, plafonds horaire et quotidien, suspension après un blocage (voir ImportThrottle).
                // Un morceau bloqué par YouTube est remis en file et retenté après la suspension, deux fois au plus.
                for (var attempt = 0; ; attempt++)
                {
                    SetPhase(job, t, "waiting", null);

                    // Pas de créneau (ni de délai) gaspillé pour un morceau déjà retiré du lot ou un import arrêté.
                    if (ct.IsCancellationRequested || !ItemExists(batch, t.ItemId))
                    {
                        Set(job, t, "skipped", "Retiré du lot avant le téléchargement.");
                        break;
                    }

                    await ImportThrottle.WaitTurnAsync(ctx, ct).ConfigureAwait(false);
                    if (!await DownloadOneAsync(job, batch, t, ctx, covers, logger, attempt >= 2, ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Import arrêté.
            }
            finally
            {
                gate.Release();
            }
        }));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (job.State == "downloading")
        {
            int ok, failed;
            lock (job.Sync)
            {
                ok = job.Tracks.Count(t => t.State == "done");
                failed = job.Tracks.Count(t => t.State == "failed");
            }

            Finish(job, "done", failed > 0 ? $"{ok} morceau(x) reçu(s), {failed} échec(s)." : $"{ok} morceau(x) reçu(s).");
        }
    }

    // Renvoie vrai si YouTube a limité le serveur et que le morceau doit être retenté après la suspension.
    private static async Task<bool> DownloadOneAsync(ImportJob job, Batch batch, ImportTrackState t, ToolContext ctx, ConcurrentDictionary<string, byte[]> covers, ILogger logger, bool lastChance, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !ItemExists(batch, t.ItemId))
        {
            Set(job, t, "skipped", "Retiré du lot avant le téléchargement.");
            return false;
        }

        Set(job, t, "downloading", null);
        SetPhase(job, t, "searching", null);
        SetItemState(batch, t.ItemId, "downloading", null);
        var work = Path.Combine(Path.GetTempPath(), "mu-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var file = await WithRetryAsync(
                c => YtDlpClient.DownloadAsync(c, t.Spec, work, ct, (phase, percent) => SetPhase(job, t, phase, percent)), ctx, work, t, logger, ct).ConfigureAwait(false);
            SetPhase(job, t, "tagging", null);
            var meta = ImportNaming.Resolve(t.Spec, job.Title, job.Layout, PositionOf(job, t));
            // Sans pochette propre au morceau, celle de la liste sert quand tout est rangé dans un album du nom de la liste.
            var coverUrl = t.Spec.CoverUrl ?? (job.Layout == "playlist" ? job.CoverUrl : null);
            var cover = coverUrl is null ? null : await GetCoverAsync(covers, coverUrl, logger, ct).ConfigureAwait(false);
            try
            {
                TrackTagger.Write(file, t.Spec.Title, meta.Artist, meta.AlbumArtist, meta.Album, meta.Track, meta.Disc, meta.Year, cover);
            }
            catch (Exception ex)
            {
                // Un fichier sans tags reste utilisable : le plan retombe sur les valeurs prévues.
                logger.LogWarning(ex, "MediaUploader: tags non écrits pour {Title}", t.Spec.Title);
            }

            ct.ThrowIfCancellationRequested();
            SetPhase(job, t, "filing", null);
            if (Register(job, batch, t, file, ctx))
            {
                ImportThrottle.ReportSuccess();
                Set(job, t, "done", null);
            }
            else
            {
                Set(job, t, "skipped", "Retiré du lot pendant le téléchargement.");
            }
        }
        catch (OperationCanceledException)
        {
            Set(job, t, "skipped", "Annulé.");
        }
        catch (ImportException ex) when (!lastChance && (ImportThrottle.IsBlock(ex.Message) || ImportThrottle.IsBlock(ex.Details)))
        {
            // YouTube limite ou soupçonne le serveur : tout est suspendu, et ce morceau est remis en file pour être retenté après la pause.
            var pause = ImportThrottle.ReportBlocked();
            logger.LogWarning("MediaUploader: YouTube limite le serveur (« {Message} ») : téléchargements suspendus {Minutes} min", ex.Message, (int)pause.TotalMinutes);
            var note = $"YouTube limite les requêtes : reprise automatique après {(int)Math.Ceiling(pause.TotalMinutes)} min.";
            Set(job, t, "queued", note);
            SetItemState(batch, t.ItemId, "queued", note);
            return true;
        }
        catch (ImportException ex)
        {
            logger.LogWarning("MediaUploader: échec d'import de {Title} : {Message}\n{Details}", t.Spec.Title, ex.Message, ex.Details ?? string.Empty);
            Fail(job, batch, t, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MediaUploader: échec d'import de {Title}", t.Spec.Title);
            Fail(job, batch, t, "Erreur inattendue (détails dans le journal de Jellyfin).");
        }
        finally
        {
            TryDeleteDir(work);
        }

        return false;
    }

    // Une pochette par adresse, téléchargée une seule fois pour tout l'import. Un échec n'est pas gardé : le morceau suivant réessaie.
    private static async Task<byte[]?> GetCoverAsync(ConcurrentDictionary<string, byte[]> covers, string url, ILogger logger, CancellationToken ct)
    {
        if (covers.TryGetValue(url, out var known))
        {
            return known;
        }

        var bytes = await TrackTagger.FetchCoverAsync(url, ct, logger).ConfigureAwait(false);
        if (bytes is not null)
        {
            covers[url] = bytes;
        }

        return bytes;
    }

    // Un échec est souvent passager (limitation, coupure) : une deuxième tentative, avec journalisation détaillée pour connaître la cause si elle échoue aussi.
    private static async Task<string> WithRetryAsync(Func<ToolContext, Task<string>> attempt, ToolContext ctx, string work, ImportTrackState t, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await attempt(ctx).ConfigureAwait(false);
        }
        catch (ImportException first) when (!ImportThrottle.IsBlock(first.Message) && !ImportThrottle.IsBlock(first.Details))
        {
            logger.LogInformation("MediaUploader: nouvelle tentative pour {Title} après : {Message}", t.Spec.Title, first.Message);
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            foreach (var f in Directory.EnumerateFiles(work))
            {
                UploadStore.TryDelete(f);
            }

            return await attempt(ctx.WithVerbose()).ConfigureAwait(false);
        }
    }

    // Déplace le fichier terminé dans la bibliothèque sous forme de .part caché (comme un envoi) et complète l'élément du lot.
    private static bool Register(ImportJob job, Batch batch, ImportTrackState t, string file, ToolContext ctx)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        Directory.CreateDirectory(ctx.MusicRoot);
        var part = Path.Combine(ctx.MusicRoot, ".mu-" + Guid.NewGuid().ToString("N") + ".part");
        File.Move(file, part);
        var size = new FileInfo(part).Length;
        var read = AudioTagReader.ReadInfo(part, ext);

        var keep = false;
        lock (batch.Sync)
        {
            var item = batch.Items.FirstOrDefault(i => i.Id == t.ItemId);
            if (item is not null && UploadStore.Batches.ContainsKey(batch.Id))
            {
                var meta = ImportNaming.Resolve(t.Spec, job.Title, job.Layout, PositionOf(job, t));
                item.TempPath = part;
                item.Size = size;
                item.Received = size;
                item.Ext = ext;
                item.ClientPath = ImportNaming.ClientPath(meta, t.Spec.Title, ext);
                item.Tags = read ?? item.Tags;
                item.ImportState = null;
                item.ImportMessage = null;
                item.Complete = true;
                keep = true;
            }
        }

        if (!keep)
        {
            UploadStore.TryDelete(part);
            return false;
        }

        batch.LastActivity = DateTime.UtcNow;
        batch.Invalidate();
        return true;
    }

    private static void Fail(ImportJob job, Batch batch, ImportTrackState t, string message)
    {
        Set(job, t, "failed", message);
        lock (batch.Sync)
        {
            var item = batch.Items.FirstOrDefault(i => i.Id == t.ItemId);
            if (item is not null)
            {
                batch.Items.Remove(item);
                batch.Overrides.Remove(item.Id);
                UploadStore.TryDelete(item.TempPath);
            }
        }

        batch.Invalidate();
    }

    private static int PositionOf(ImportJob job, ImportTrackState t)
    {
        lock (job.Sync)
        {
            return job.Tracks.IndexOf(t) + 1;
        }
    }

    private static bool ItemExists(Batch batch, string? itemId)
    {
        lock (batch.Sync)
        {
            return itemId is not null && batch.Items.Any(i => i.Id == itemId);
        }
    }

    private static void SetItemState(Batch batch, string? itemId, string state, string? message)
    {
        lock (batch.Sync)
        {
            var item = batch.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is not null)
            {
                item.ImportState = state;
                item.ImportMessage = message;
            }
        }

        batch.LastActivity = DateTime.UtcNow;
        batch.Invalidate();
    }

    private static void SetPhase(ImportJob job, ImportTrackState t, string phase, int? percent)
    {
        lock (job.Sync)
        {
            t.Phase = phase;
            t.Percent = percent;
            job.Updated = DateTime.UtcNow;
        }
    }

    private static void Set(ImportJob job, ImportTrackState t, string state, string? message)
    {
        lock (job.Sync)
        {
            t.State = state;
            t.Message = message;
            t.Phase = state == "queued" ? "waiting" : state == "downloading" ? t.Phase : null;
            t.Percent = null;
            job.Updated = DateTime.UtcNow;
        }
    }

    private static void Finish(ImportJob job, string state, string message)
    {
        lock (job.Sync)
        {
            if (job.State is "done" or "failed" or "cancelled")
            {
                return;
            }

            job.State = state;
            job.Message = message;
            job.Updated = DateTime.UtcNow;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage best effort.
        }
    }
}
