using System.Collections.Concurrent;
using Jellyfin.Plugin.MediaUploader.Configuration;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Un fichier d'un lot en attente : son état de réception et ce qu'on en a lu.
/// </summary>
public sealed class BatchItem
{
    /// <summary>Gets l'identifiant (stable pendant tout le lot).</summary>
    public required string Id { get; init; }

    /// <summary>Gets le chemin relatif côté client.</summary>
    public required string ClientPath { get; init; }

    /// <summary>Gets la taille annoncée.</summary>
    public required long Size { get; init; }

    /// <summary>Gets l'extension d'origine, en minuscules.</summary>
    public required string Ext { get; init; }

    /// <summary>Gets or sets le fichier partiel dans la bibliothèque (créé à la réception du premier morceau).</summary>
    public string? TempPath { get; set; }

    /// <summary>Gets or sets le nombre d'octets reçus et validés.</summary>
    public long Received { get; set; }

    /// <summary>Gets or sets a value indicating whether le fichier est entièrement reçu et analysé.</summary>
    public bool Complete { get; set; }

    /// <summary>Gets or sets les tags lus (audio).</summary>
    public TagInfo? Tags { get; set; }

    /// <summary>Gets le verrou qui sérialise les écritures d'un même fichier.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Un lot : des fichiers envoyés et analysés, en attente de confirmation par l'utilisateur.
/// </summary>
public sealed class Batch
{
    /// <summary>Gets l'identifiant du lot.</summary>
    public required string Id { get; init; }

    /// <summary>Gets l'utilisateur (ou la clé API) propriétaire du lot.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets or sets le type choisi : "auto", "music", "movie" ou "series".</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Gets or sets la date de dernière activité (UTC).</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>Gets les fichiers du lot (à manipuler sous <see cref="Sync"/>).</summary>
    public List<BatchItem> Items { get; } = new();

    /// <summary>Gets les corrections de l'utilisateur (à manipuler sous <see cref="Sync"/>).</summary>
    public Dictionary<string, ItemOverride> Overrides { get; } = new();

    /// <summary>Gets l'objet de verrouillage des structures du lot.</summary>
    public object Sync { get; } = new();

    /// <summary>Gets le verrou qui empêche deux confirmations simultanées.</summary>
    public SemaphoreSlim CommitLock { get; } = new(1, 1);

    private Plan? _cached;
    private DateTime _cachedAt;

    /// <summary>
    /// Plan courant. Mis en cache quelques secondes tant que rien ne change (le disque peut changer à côté).
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="fresh">Force un recalcul (confirmation).</param>
    /// <returns>Plan.</returns>
    public Plan GetPlan(PluginConfiguration config, bool fresh = false)
    {
        lock (Sync)
        {
            if (!fresh && _cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(15))
            {
                return _cached;
            }

            var files = Items.Select(i => new PlanFile
            {
                Id = i.Id,
                ClientPath = i.ClientPath,
                Size = i.Size,
                Tags = i.Tags,
                Analyzed = i.Complete
            }).ToList();

            var options = PlanFactory.Options(config);
            _cached = Planner.Build(files, new Dictionary<string, ItemOverride>(Overrides), Mode, options, PlanFactory.Engine(config), PlanFactory.Probe());
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>
    /// Oublie le plan en cache (après une correction, un fichier analysé ou retiré).
    /// </summary>
    public void Invalidate()
    {
        lock (Sync)
        {
            _cached = null;
        }
    }
}

/// <summary>
/// Envois en cours, gardés en mémoire (les fichiers partiels sont des .part dans les dossiers de bibliothèque).
/// </summary>
public static class UploadStore
{
    /// <summary>Durée d'inactivité après laquelle un envoi direct par morceaux est abandonné.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(2);

    /// <summary>Âge après lequel un .part que personne ne réclame est supprimé.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    /// <summary>Gets les envois directs par morceaux.</summary>
    public static ConcurrentDictionary<string, ChunkSession> Sessions { get; } = new();

    /// <summary>Gets les lots en attente de confirmation.</summary>
    public static ConcurrentDictionary<string, Batch> Batches { get; } = new();

    /// <summary>
    /// Supprime les envois abandonnés, les lots expirés et les .part orphelins des dossiers de bibliothèque.
    /// </summary>
    /// <param name="config">Configuration.</param>
    public static void Sweep(PluginConfiguration config)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, session) in Sessions)
        {
            if (now - session.LastActivity > SessionTimeout && Sessions.TryRemove(id, out var removed))
            {
                TryDelete(removed.TempPath);
            }
        }

        var ttl = TimeSpan.FromHours(Math.Clamp(config.BatchTtlHours <= 0 ? 6 : config.BatchTtlHours, 1, 72));
        foreach (var (id, batch) in Batches)
        {
            if (now - batch.LastActivity > ttl && Batches.TryRemove(id, out var removed))
            {
                lock (removed.Sync)
                {
                    foreach (var item in removed.Items)
                    {
                        TryDelete(item.TempPath);
                    }
                }
            }
        }

        var active = new HashSet<string>(Sessions.Values.Select(s => s.TempPath), StringComparer.Ordinal);
        foreach (var batch in Batches.Values)
        {
            lock (batch.Sync)
            {
                foreach (var item in batch.Items.Where(i => i.TempPath is not null))
                {
                    active.Add(item.TempPath!);
                }
            }
        }

        foreach (var root in new[] { config.MusicPath, config.MoviesPath, config.ShowsPath })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, ".mu-*.part"))
                {
                    if (!active.Contains(file) && now - File.GetLastWriteTimeUtc(file) > OrphanAge)
                    {
                        TryDelete(file);
                    }
                }
            }
            catch (IOException)
            {
                // Nettoyage best effort.
            }
        }
    }

    /// <summary>
    /// Supprime un fichier sans lever d'exception.
    /// </summary>
    /// <param name="path">Chemin (peut être null).</param>
    public static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nettoyage best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Nettoyage best effort.
        }
    }
}
