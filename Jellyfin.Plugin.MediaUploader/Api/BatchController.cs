using System.Net.Mime;
using Jellyfin.Plugin.MediaUploader.Services;
using Jellyfin.Plugin.MediaUploader.Services.Import;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// Envoi en deux temps, utilisé par la page web : les fichiers sont reçus et analysés dans un lot (aucun n'entre dans la
/// bibliothèque), le serveur propose le rangement (groupes, sous-titres associés), l'utilisateur corrige, puis confirme.
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class BatchController : MediaUploaderControllerBase
{
    private const int MaxFilesPerBatch = 3000;
    private const int MaxBatchesPerOwner = 10;

    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILogger<BatchController> _logger;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="BatchController"/>.
    /// </summary>
    /// <param name="libraryManager">Gestionnaire de bibliothèque.</param>
    /// <param name="playlistManager">Gestionnaire de listes de lecture.</param>
    /// <param name="logger">Logger.</param>
    public BatchController(ILibraryManager libraryManager, IPlaylistManager playlistManager, ILogger<BatchController> logger)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _logger = logger;
    }

    /// <summary>
    /// Crée un lot à partir de la liste des fichiers (noms et tailles, sans octets) et renvoie le premier plan, calculé sur les noms.
    /// </summary>
    /// <param name="request">Fichiers et type choisi.</param>
    /// <returns>Lot, avec les identifiants attribués.</returns>
    [HttpPost("Batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<BatchResponse> Create([FromBody] CreateBatchRequest request)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var config = Config;
        var mode = PlanFactory.NormalizeMode(request.Mode);
        if (mode is null)
        {
            return BadRequest(new { error = "Le champ 'mode' doit valoir 'auto', 'music', 'movie' ou 'series'." });
        }

        if (request.Files.Count == 0 || request.Files.Count > MaxFilesPerBatch)
        {
            return BadRequest(new { error = $"Un lot contient de 1 à {MaxFilesPerBatch} fichiers." });
        }

        UploadStore.Sweep(config);
        if (UploadStore.Batches.Values.Count(b => b.Owner == CurrentOwner) >= MaxBatchesPerOwner)
        {
            return BadRequest(new { error = "Trop de lots en attente : confirmez ou annulez les précédents." });
        }

        var batch = new Batch { Id = Guid.NewGuid().ToString("N"), Owner = CurrentOwner, Mode = mode };
        var ids = new List<string>();
        lock (batch.Sync)
        {
            for (var i = 0; i < request.Files.Count; i++)
            {
                var f = request.Files[i];
                var path = NameTools.NormalizePath(f.Path);
                var id = "i" + i;
                ids.Add(id);
                batch.Items.Add(new BatchItem
                {
                    Id = id,
                    ClientPath = path,
                    Size = Math.Max(0, f.Size),
                    Ext = Path.GetExtension(NameTools.FileNameOf(path)).ToLowerInvariant()
                });
            }
        }

        UploadStore.Batches[batch.Id] = batch;
        return BuildResponse(batch, ids);
    }

    /// <summary>
    /// État d'un lot : plan courant et avancement de la réception.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <returns>Lot.</returns>
    [HttpGet("Batch/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BatchResponse> Get(string id)
    {
        var batch = Find(id, out var error);
        if (batch is null)
        {
            return error!;
        }

        batch.LastActivity = DateTime.UtcNow;
        lock (batch.Sync)
        {
            return BuildResponse(batch, batch.Items.Select(i => i.Id).ToList());
        }
    }

    /// <summary>
    /// Enregistre les corrections de l'utilisateur (elles remplacent les précédentes) et renvoie le plan recalculé.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="request">Corrections par identifiant de fichier, et type de lot éventuel.</param>
    /// <returns>Lot.</returns>
    [HttpPut("Batch/{id}/Overrides")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BatchResponse> SetOverrides(string id, [FromBody] OverridesRequest request)
    {
        var batch = Find(id, out var error);
        if (batch is null)
        {
            return error!;
        }

        lock (batch.Sync)
        {
            if (request.Mode is not null)
            {
                var mode = PlanFactory.NormalizeMode(request.Mode);
                if (mode is null)
                {
                    return BadRequest(new { error = "Le champ 'mode' doit valoir 'auto', 'music', 'movie' ou 'series'." });
                }

                batch.Mode = mode;
            }

            if (request.Overrides is not null)
            {
                var known = batch.Items.Select(i => i.Id).ToHashSet();
                batch.Overrides.Clear();
                foreach (var (key, value) in request.Overrides.Where(kv => known.Contains(kv.Key) && kv.Value is not null))
                {
                    batch.Overrides[key] = value;
                }
            }
        }

        batch.LastActivity = DateTime.UtcNow;
        batch.Invalidate();
        lock (batch.Sync)
        {
            return BuildResponse(batch, batch.Items.Select(i => i.Id).ToList());
        }
    }

    /// <summary>
    /// Indique combien d'octets d'un fichier du lot ont été reçus (reprise après une coupure).
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <returns>Avancement.</returns>
    [HttpGet("Batch/{id}/Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemProgress> GetItem(string id, string itemId)
    {
        var item = FindItem(id, itemId, out _, out var error);
        if (item is null)
        {
            return error!;
        }

        return new ItemProgress(item.Received, item.Size, item.Complete);
    }

    /// <summary>
    /// Reçoit un morceau d'un fichier du lot (corps brut). L'offset doit être égal au nombre d'octets déjà reçus, sinon 409.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <param name="offset">Position du morceau dans le fichier.</param>
    /// <returns>Octets reçus au total.</returns>
    [HttpPut("Batch/{id}/Items/{itemId}/Chunk")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UploadChunk(string id, string itemId, [FromQuery] long offset)
    {
        var item = FindItem(id, itemId, out var batch, out var error);
        if (item is null || batch is null)
        {
            return error!;
        }

        await item.Lock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var problem = EnsureTemp(batch, item);
            if (problem is not null)
            {
                return Conflict(new { error = problem });
            }

            if (offset != item.Received)
            {
                return Conflict(new { error = "Offset inattendu.", received = item.Received });
            }

            var (ok, received, message) = await ChunkWriter.AppendAsync(item.TempPath!, item.Received, item.Size, Request.Body, HttpContext.RequestAborted).ConfigureAwait(false);
            if (!ok)
            {
                return BadRequest(new { error = message, received });
            }

            item.Received = received;
            batch.LastActivity = DateTime.UtcNow;
            return Ok(new { received = item.Received });
        }
        finally
        {
            item.Lock.Release();
        }
    }

    /// <summary>
    /// Termine la réception d'un fichier : vérifie la taille et lit les tags (musique). Le plan se met à jour.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <returns>Avancement.</returns>
    [HttpPost("Batch/{id}/Items/{itemId}/Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ItemProgress>> CompleteItem(string id, string itemId)
    {
        var item = FindItem(id, itemId, out var batch, out var error);
        if (item is null || batch is null)
        {
            return error!;
        }

        await item.Lock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var problem = EnsureTemp(batch, item);
            if (problem is not null)
            {
                return Conflict(new { error = problem });
            }

            if (item.Received != item.Size)
            {
                return Conflict(new { error = "Envoi incomplet.", received = item.Received, size = item.Size });
            }

            if (PlanFactory.Options(Config).Audio.Contains(item.Ext))
            {
                item.Tags = AudioTagReader.ReadInfo(item.TempPath!, item.Ext);
            }

            item.Complete = true;
            batch.LastActivity = DateTime.UtcNow;
            batch.Invalidate();
            return new ItemProgress(item.Received, item.Size, true);
        }
        finally
        {
            item.Lock.Release();
        }
    }

    /// <summary>
    /// Retire un fichier du lot et supprime sa copie temporaire.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <returns>Lot mis à jour.</returns>
    [HttpDelete("Batch/{id}/Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BatchResponse> RemoveItem(string id, string itemId)
    {
        var item = FindItem(id, itemId, out var batch, out var error);
        if (item is null || batch is null)
        {
            return error!;
        }

        lock (batch.Sync)
        {
            batch.Items.Remove(item);
            batch.Overrides.Remove(itemId);
            foreach (var other in batch.Overrides.Values.Where(o => o.PairWith == itemId))
            {
                other.PairWith = null;
            }
        }

        UploadStore.TryDelete(item.TempPath);
        batch.Invalidate();
        lock (batch.Sync)
        {
            return BuildResponse(batch, batch.Items.Select(i => i.Id).ToList());
        }
    }

    /// <summary>
    /// Confirme : les fichiers choisis (entièrement reçus) sont rangés dans la bibliothèque selon le plan recalculé à cet instant.
    /// Les autres restent dans le lot.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="request">Fichiers à enregistrer (tous si omis) et scan éventuel.</param>
    /// <returns>Résultat par fichier.</returns>
    [HttpPost("Batch/{id}/Commit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UploadResponse>> Commit(string id, [FromBody] CommitRequest request)
    {
        var batch = Find(id, out var error);
        if (batch is null)
        {
            return error!;
        }

        var config = Config;
        await batch.CommitLock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            // Plan recalculé maintenant : un fichier a pu apparaître dans la bibliothèque depuis l'aperçu.
            var plan = batch.GetPlan(config, fresh: true);
            var wanted = request.ItemIds?.ToHashSet(StringComparer.Ordinal);
            var selected = plan.Groups.SelectMany(g => g.Items).Where(i => wanted is null || wanted.Contains(i.Id)).ToList();

            List<BatchItem> snapshot;
            lock (batch.Sync)
            {
                snapshot = batch.Items.ToList();
            }

            var byId = snapshot.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var work = selected.Select(i => (Item: i, Temp: byId.TryGetValue(i.Id, out var bi) && bi.Complete ? bi.TempPath : null)).ToList();
            var results = UploadCommitter.Commit(work, config, _logger);

            // Sortent du lot les fichiers rangés, et ceux dont la copie temporaire a disparu après une erreur.
            lock (batch.Sync)
            {
                for (var i = 0; i < work.Count; i++)
                {
                    var tempGone = work[i].Temp is { } t && !System.IO.File.Exists(t);
                    var done = results[i].Status == "saved" || (results[i].Status == "error" && tempGone);
                    if (done && byId.TryGetValue(work[i].Item.Id, out var removed))
                    {
                        batch.Items.Remove(removed);
                        batch.Overrides.Remove(removed.Id);
                    }
                }
            }

            batch.Invalidate();
            batch.LastActivity = DateTime.UtcNow;
            bool empty;
            lock (batch.Sync)
            {
                empty = batch.Items.Count == 0;
            }

            if (empty)
            {
                UploadStore.Batches.TryRemove(batch.Id, out _);
            }

            var saved = results.Count(r => r.Status == "saved");
            var scanQueued = false;
            if (saved > 0 && (request.Scan ?? config.AutoScan))
            {
                _libraryManager.QueueLibraryScan();
                scanQueued = true;
            }

            // Import de playlist : la liste de lecture Jellyfin est créée dès que le scan a fait apparaître les morceaux.
            if (saved > 0 && batch.Playlist is { } playlist)
            {
                var position = playlist.Order.Select((itemId, index) => (itemId, index)).ToDictionary(x => x.itemId, x => x.index, StringComparer.Ordinal);
                var paths = work.Select((w, i) => (w.Item.Id, Result: results[i]))
                    .Where(x => x.Result.Status == "saved" && x.Result.Path is not null && position.ContainsKey(x.Id))
                    .OrderBy(x => position[x.Id])
                    .Select(x => x.Result.Path!)
                    .ToList();
                PlaylistBuilder.Queue(_libraryManager, _playlistManager, playlist, paths, _logger);
            }

            return new UploadResponse(saved, scanQueued, results);
        }
        finally
        {
            batch.CommitLock.Release();
        }
    }

    /// <summary>
    /// Annule le lot : supprime toutes les copies temporaires. Rien n'est entré dans la bibliothèque.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <returns>204.</returns>
    [HttpDelete("Batch/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Cancel(string id)
    {
        var batch = Find(id, out var error);
        if (batch is null)
        {
            return error!;
        }

        UploadStore.Batches.TryRemove(batch.Id, out _);
        ImportManager.CancelForBatch(batch.Id);
        lock (batch.Sync)
        {
            foreach (var item in batch.Items)
            {
                UploadStore.TryDelete(item.TempPath);
            }

            batch.Items.Clear();
        }

        return NoContent();
    }

    // Crée le fichier partiel au premier morceau, dans la racine de destination du fichier d'après le plan courant.
    private string? EnsureTemp(Batch batch, BatchItem item)
    {
        if (item.TempPath is not null)
        {
            return null;
        }

        var planned = batch.GetPlan(Config).Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == item.Id);
        if (planned is null || planned.Status is "skipped" or "error" || planned.Root is null)
        {
            return planned is null ? "Fichier inconnu." : "Ce fichier est ignoré : " + string.Join(" · ", planned.Notes);
        }

        var tmp = Path.Combine(planned.Root, ".mu-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Directory.CreateDirectory(planned.Root);
            using (new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaUploader: impossible de créer {Path}", tmp);
            return ex.Message;
        }

        item.TempPath = tmp;
        return null;
    }

    private ActionResult<BatchResponse> BuildResponse(Batch batch, IReadOnlyList<string> ids)
    {
        var config = Config;
        var plan = batch.GetPlan(config);
        Dictionary<string, ItemProgress> progress;
        lock (batch.Sync)
        {
            progress = batch.Items.ToDictionary(i => i.Id, i => new ItemProgress(i.Received, i.Size, i.Complete, i.ImportState, i.ImportMessage), StringComparer.Ordinal);
        }

        return new BatchResponse(batch.Id, ChunkMb(config) * 1024 * 1024, ids, plan, progress);
    }

    private Batch? Find(string id, out ActionResult? error)
    {
        error = null;
        if (!CanUpload)
        {
            error = Forbid();
            return null;
        }

        if (!UploadStore.Batches.TryGetValue(id, out var batch))
        {
            error = NotFound(new { error = "Lot inconnu ou expiré : recommencez." });
            return null;
        }

        if (batch.Owner != CurrentOwner && !IsAdmin)
        {
            error = Forbid();
            return null;
        }

        return batch;
    }

    /// <summary>
    /// Tags lus dans un fichier audio entièrement reçu (titre, artistes, album, numéros, année, durée, présence de pochette), pour vérifier avant de confirmer.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <returns>Tags.</returns>
    [HttpGet("Batch/{id}/Items/{itemId}/Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetItemPreview(string id, string itemId)
    {
        var details = ReadItemDetails(id, itemId, out var error);
        if (details is null)
        {
            return error!;
        }

        return Ok(new
        {
            details.Title,
            details.Artist,
            details.AlbumArtist,
            details.Album,
            Track = details.Track == 0 ? (uint?)null : details.Track,
            Disc = details.Disc == 0 ? (uint?)null : details.Disc,
            Year = details.Year == 0 ? (uint?)null : details.Year,
            details.DurationSec,
            HasCover = details.CoverData is not null,
            CoverBytes = details.CoverData?.Length ?? 0
        });
    }

    /// <summary>
    /// Pochette intégrée d'un fichier audio reçu.
    /// </summary>
    /// <param name="id">Identifiant du lot.</param>
    /// <param name="itemId">Identifiant du fichier.</param>
    /// <returns>Image.</returns>
    [HttpGet("Batch/{id}/Items/{itemId}/Cover")]
    [Produces("image/jpeg", "image/png", "image/webp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetItemCover(string id, string itemId)
    {
        var details = ReadItemDetails(id, itemId, out var error);
        if (details is null)
        {
            return error!;
        }

        if (details.CoverData is null || details.CoverMime is null)
        {
            return NotFound(new { error = "Pas de pochette dans ce fichier." });
        }

        Response.Headers["Cache-Control"] = "private, max-age=300";
        return File(details.CoverData, details.CoverMime);
    }

    private AudioDetails? ReadItemDetails(string id, string itemId, out ActionResult? error)
    {
        var item = FindItem(id, itemId, out var batch, out error);
        if (item is null || batch is null)
        {
            return null;
        }

        string? path;
        bool complete;
        lock (batch.Sync)
        {
            path = item.TempPath;
            complete = item.Complete;
        }

        if (!complete || path is null || !System.IO.File.Exists(path))
        {
            error = NotFound(new { error = "Fichier pas encore reçu." });
            return null;
        }

        var details = AudioTagReader.ReadDetails(path, item.Ext);
        if (details is null)
        {
            error = NotFound(new { error = "Tags illisibles." });
        }

        return details;
    }

    private BatchItem? FindItem(string id, string itemId, out Batch? batch, out ActionResult? error)
    {
        batch = Find(id, out error);
        if (batch is null)
        {
            return null;
        }

        BatchItem? item;
        lock (batch.Sync)
        {
            item = batch.Items.FirstOrDefault(i => i.Id == itemId);
        }

        if (item is null)
        {
            error = NotFound(new { error = "Fichier inconnu dans ce lot." });
        }

        return item;
    }
}
