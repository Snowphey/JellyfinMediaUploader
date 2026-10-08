using System.Net.Mime;
using Jellyfin.Plugin.MediaUploader.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// API d'upload direct (scripts, yt-dlp...) : le fichier est analysé puis rangé tout de suite, sans confirmation.
/// La page web, elle, passe par les lots (<see cref="BatchController"/>) pour montrer le rangement avant d'envoyer.
/// Authentification : session d'un utilisateur (tout le monde si autorisé dans les paramètres)
/// ou clé API Jellyfin (<c>X-Emby-Token: CLE</c> ou <c>Authorization: MediaBrowser Token="CLE"</c>).
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class UploadController : MediaUploaderControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<UploadController> _logger;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="UploadController"/>.
    /// </summary>
    /// <param name="libraryManager">Gestionnaire de bibliothèque.</param>
    /// <param name="logger">Logger.</param>
    public UploadController(ILibraryManager libraryManager, ILogger<UploadController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Page d'upload (ouverte directement ou intégrée dans un onglet par un plugin tiers).
    /// </summary>
    /// <returns>HTML.</returns>
    [HttpGet("Ui")]
    [AllowAnonymous]
    [Produces("text/html")]
    public ActionResult GetUi()
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.ui.html");
        if (stream is null)
        {
            return new ContentResult { StatusCode = StatusCodes.Status404NotFound, Content = "Ressource introuvable" };
        }

        using var reader = new StreamReader(stream);
        Response.Headers["Cache-Control"] = "no-cache";
        return new ContentResult { Content = reader.ReadToEnd(), ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status200OK };
    }

    /// <summary>
    /// Retourne l'état de la configuration.
    /// </summary>
    /// <returns>État.</returns>
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var c = Config;
        var mode = (c.ConfirmationMode ?? "always").Trim().ToLowerInvariant();
        return new StatusResponse(
            !string.IsNullOrWhiteSpace(c.MusicPath),
            !string.IsNullOrWhiteSpace(c.MoviesPath),
            c.AudioExtensions,
            c.VideoExtensions,
            c.ExtraExtensions,
            c.MaxFileSizeMb,
            c.AutoScan,
            CanUpload,
            ChunkMb(c),
            !string.IsNullOrWhiteSpace(c.ShowsPath),
            mode is "doubtful" or "never" ? mode : "always",
            Math.Clamp(c.BatchTtlHours <= 0 ? 6 : c.BatchTtlHours, 1, 72));
    }

    /// <summary>
    /// Artistes (dossiers de premier niveau) et, si un artiste est donné, albums existants dans la bibliothèque musique :
    /// sert à l'autocomplétion pour réutiliser un nom déjà présent plutôt que d'en créer une variante.
    /// </summary>
    /// <param name="artist">Artiste dont on veut les albums (facultatif).</param>
    /// <returns>Artistes et albums.</returns>
    [HttpGet("Music/Suggestions")]
    public ActionResult GetMusicSuggestions([FromQuery] string? artist)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var root = Config.MusicPath?.Trim();
        var artists = new List<string>();
        var albums = new List<string>();
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                artists = Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith('.'))
                    .Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(5000).ToList();

                // Le nom saisi n'est jamais utilisé comme chemin : on le compare aux dossiers existants.
                var match = string.IsNullOrWhiteSpace(artist) ? null : artists.FirstOrDefault(a => string.Equals(a, PathBuilder.SanitizeSegment(artist, string.Empty), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    albums = Directory.EnumerateDirectories(Path.Combine(root, match)).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(2000).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Suggestions facultatives : on renvoie ce qu'on a.
            }
        }

        return Ok(new { Artists = artists, Albums = albums });
    }

    /// <summary>
    /// Titres déjà présents dans la bibliothèque films ou séries (noms de dossiers « Titre (Année) »), pour l'autocomplétion.
    /// </summary>
    /// <param name="kind">"movie" ou "series".</param>
    /// <returns>Titres et années.</returns>
    [HttpGet("Library/Titles")]
    public ActionResult GetLibraryTitles([FromQuery] string? kind)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var root = (PlanFactory.NormalizeMode(kind) == "series" ? Config.ShowsPath : Config.MoviesPath)?.Trim();
        var titles = new List<object>();
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                var maxYear = DateTime.UtcNow.Year + 2;
                foreach (var name in Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('.')).Take(5000))
                {
                    var (title, year) = NameTools.SplitYear(name!, maxYear);
                    titles.Add(new { Title = title, Year = year });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Suggestions facultatives.
            }
        }

        return Ok(new { Titles = titles });
    }

    /// <summary>
    /// Lance un scan de la bibliothèque.
    /// </summary>
    /// <returns>204.</returns>
    [HttpPost("Scan")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Scan()
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        _libraryManager.QueueLibraryScan();
        return NoContent();
    }

    /// <summary>
    /// Envoie un ou plusieurs fichiers vers la bibliothèque musique, films ou séries, sans confirmation.
    /// Les fichiers d'une même requête sont analysés ensemble (un sous-titre est rattaché à sa vidéo). Artiste/album (musique),
    /// titre/année (film), titre/année/saison (série) sont déduits des tags et des noms ; les champs du formulaire les remplacent si fournis.
    /// </summary>
    /// <param name="request">Formulaire.</param>
    /// <returns>Résultat par fichier.</returns>
    [HttpPost("Upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadResponse>> Upload([FromForm] UploadRequest request)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var config = Config;
        if (request.Files.Count == 0)
        {
            return BadRequest(new { error = "Aucun fichier reçu (champ 'files')." });
        }

        var mode = PlanFactory.NormalizeMode(request.Type);
        if (mode is null)
        {
            return BadRequest(new { error = "Le champ 'type' doit valoir 'music', 'movie' ou 'series' (ou être omis)." });
        }

        var options = PlanFactory.Options(config);
        var engine = PlanFactory.Engine(config);
        var fs = PlanFactory.Probe();
        var planFiles = request.Files
            .Select((f, i) => new PlanFile { Id = "u" + i, ClientPath = Path.GetFileName(f.FileName ?? string.Empty), Size = f.Length })
            .ToList();
        var overrides = BuildOverrides(planFiles.Select(p => p.Id), request.Title, request.Year, request.Season, request.Artist, request.Album, request.Force);

        // 1. Premier calcul sur les noms : quels fichiers sont acceptés, et dans quel dossier les stocker en attendant.
        var pre = Planner.Build(planFiles, overrides, mode, options, engine, fs);
        var rejected = new Dictionary<string, UploadFileResult>();
        var temps = new Dictionary<string, string>();

        try
        {
            foreach (var item in pre.Groups.SelectMany(g => g.Items))
            {
                if (item.Status is "skipped" or "error")
                {
                    rejected[item.Id] = new UploadFileResult(item.Name, item.Status, null, string.Join(" · ", item.Notes));
                    continue;
                }

                var file = request.Files[int.Parse(item.Id[1..], System.Globalization.CultureInfo.InvariantCulture)];
                var tmp = Path.Combine(item.Root!, ".mu-" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    // Fichier temporaire dans le dossier de la bibliothèque (même disque : le déplacement final est instantané).
                    // Le nom ne finit pas par une extension média, donc Jellyfin ne l'indexe pas pendant l'écriture.
                    Directory.CreateDirectory(item.Root!);
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await file.CopyToAsync(output, HttpContext.RequestAborted).ConfigureAwait(false);
                    }

                    temps[item.Id] = tmp;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    UploadStore.TryDelete(tmp);
                    _logger.LogError(ex, "MediaUploader: échec d'enregistrement de {Name}", item.Name);
                    rejected[item.Id] = new UploadFileResult(item.Name, "error", null, ex.Message);
                }
            }

            // 2. Les fichiers sont là : on lit les tags et on recalcule le plan complet (tags, sous-titres, collisions).
            var received = planFiles.Where(p => temps.ContainsKey(p.Id)).ToList();
            foreach (var pf in received)
            {
                var ext = Path.GetExtension(pf.ClientPath).ToLowerInvariant();
                if (options.Audio.Contains(ext))
                {
                    pf.Tags = AudioTagReader.ReadInfo(temps[pf.Id], ext);
                }

                pf.Analyzed = true;
            }

            var plan = Planner.Build(received, overrides, mode, options, engine, fs);
            var items = plan.Groups.SelectMany(g => g.Items).Select(i => (Item: i, Temp: (string?)temps[i.Id])).ToList();
            var committed = UploadCommitter.Commit(items, config, _logger);

            var results = new Dictionary<string, UploadFileResult>(rejected);
            for (var i = 0; i < items.Count; i++)
            {
                results[items[i].Item.Id] = committed[i];
                if (committed[i].Status != "saved")
                {
                    UploadStore.TryDelete(items[i].Temp);
                }
            }

            var saved = results.Values.Count(r => r.Status == "saved");
            var scanQueued = false;
            if (saved > 0 && (request.Scan ?? config.AutoScan))
            {
                _libraryManager.QueueLibraryScan();
                scanQueued = true;
            }

            return new UploadResponse(saved, scanQueued, planFiles.Select(p => results[p.Id]).ToList());
        }
        catch (OperationCanceledException)
        {
            foreach (var tmp in temps.Values)
            {
                UploadStore.TryDelete(tmp);
            }

            throw;
        }
    }

    /// <summary>
    /// Démarre un envoi direct par morceaux (gros fichiers, proxys limitant la taille des requêtes), sans confirmation.
    /// Enchaîner ensuite des <c>PUT Upload/{id}/Chunk?offset=N</c> puis <c>POST Upload/{id}/Complete</c>.
    /// </summary>
    /// <param name="request">Métadonnées du fichier.</param>
    /// <returns>Identifiant et taille de morceau conseillée.</returns>
    [HttpPost("Upload/Start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ChunkUploadState> StartUpload([FromBody] StartUploadRequest request)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var config = Config;
        var mode = PlanFactory.NormalizeMode(request.Type);
        if (mode is null)
        {
            return BadRequest(new { error = "Le champ 'type' doit valoir 'music', 'movie' ou 'series' (ou être omis)." });
        }

        if (request.Size < 0)
        {
            return BadRequest(new { error = "Taille invalide." });
        }

        var originalName = Path.GetFileName(request.FileName ?? string.Empty);
        var file = new PlanFile { Id = "u0", ClientPath = originalName, Size = request.Size };
        var overrides = BuildOverrides(new[] { file.Id }, request.Title, request.Year, request.Season, request.Artist, request.Album, request.Force);
        var pre = Planner.Build(new[] { file }, overrides, mode, PlanFactory.Options(config), PlanFactory.Engine(config), PlanFactory.Probe());
        var item = pre.Groups.SelectMany(g => g.Items).First();
        if (item.Status is "skipped" or "error")
        {
            return BadRequest(new { error = string.Join(" · ", item.Notes), status = item.Status });
        }

        var root = item.Root!;
        UploadStore.Sweep(config);

        var id = Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(root, ".mu-" + id + ".part");
        try
        {
            Directory.CreateDirectory(root);
            using (new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaUploader: impossible de créer {Path}", tmp);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }

        UploadStore.Sessions[id] = new ChunkSession
        {
            Id = id,
            Owner = CurrentOwner,
            TempPath = tmp,
            Root = root,
            Mode = mode,
            OriginalName = originalName,
            Size = request.Size,
            Artist = request.Artist,
            Album = request.Album,
            Title = request.Title,
            Year = request.Year,
            Season = request.Season,
            Force = request.Force,
            Scan = request.Scan,
            LastActivity = DateTime.UtcNow
        };

        return new ChunkUploadState(id, 0, request.Size, ChunkMb(config) * 1024 * 1024);
    }

    /// <summary>
    /// Indique combien d'octets ont été reçus (pour reprendre un envoi interrompu).
    /// </summary>
    /// <param name="id">Identifiant de l'envoi.</param>
    /// <returns>État.</returns>
    [HttpGet("Upload/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ChunkUploadState> GetUploadState(string id)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var session = FindSession(id, out var error);
        if (session is null)
        {
            return error!;
        }

        return new ChunkUploadState(session.Id, session.Received, session.Size, ChunkMb(Config) * 1024 * 1024);
    }

    /// <summary>
    /// Reçoit un morceau (corps brut de la requête). L'offset doit être égal au nombre d'octets déjà reçus ;
    /// sinon 409 avec l'offset attendu, ce qui permet de reprendre proprement après une erreur réseau.
    /// </summary>
    /// <param name="id">Identifiant de l'envoi.</param>
    /// <param name="offset">Position du morceau dans le fichier.</param>
    /// <returns>Octets reçus au total.</returns>
    [HttpPut("Upload/{id}/Chunk")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UploadChunk(string id, [FromQuery] long offset)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var session = FindSession(id, out var error);
        if (session is null)
        {
            return error!;
        }

        await session.Lock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            if (offset != session.Received)
            {
                return Conflict(new { error = "Offset inattendu.", received = session.Received });
            }

            var (ok, received, message) = await ChunkWriter.AppendAsync(session.TempPath, session.Received, session.Size, Request.Body, HttpContext.RequestAborted).ConfigureAwait(false);
            if (!ok)
            {
                return BadRequest(new { error = message, received });
            }

            session.Received = received;
            session.LastActivity = DateTime.UtcNow;
            return Ok(new { received = session.Received });
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Termine l'envoi : vérifie la taille, analyse le fichier, le range (tags, dossiers, sous-titre rattaché à sa vidéo...) et lance éventuellement un scan.
    /// </summary>
    /// <param name="id">Identifiant de l'envoi.</param>
    /// <returns>Même réponse que <c>Upload</c> (un seul fichier).</returns>
    [HttpPost("Upload/{id}/Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UploadResponse>> CompleteUpload(string id)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var session = FindSession(id, out var error);
        if (session is null)
        {
            return error!;
        }

        var config = Config;
        await session.Lock.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            if (session.Received != session.Size)
            {
                return Conflict(new { error = "Envoi incomplet.", received = session.Received, size = session.Size });
            }

            UploadFileResult result;
            try
            {
                var options = PlanFactory.Options(config);
                var ext = Path.GetExtension(session.OriginalName).ToLowerInvariant();
                var file = new PlanFile
                {
                    Id = "u0",
                    ClientPath = session.OriginalName,
                    Size = session.Size,
                    Tags = options.Audio.Contains(ext) ? AudioTagReader.ReadInfo(session.TempPath, ext) : null,
                    Analyzed = true
                };
                var overrides = BuildOverrides(new[] { file.Id }, session.Title, session.Year, session.Season, session.Artist, session.Album, session.Force);
                var plan = Planner.Build(new[] { file }, overrides, session.Mode, options, PlanFactory.Engine(config), PlanFactory.Probe());
                result = UploadCommitter.Commit(new[] { (plan.Groups.SelectMany(g => g.Items).First(), (string?)session.TempPath) }, config, _logger)[0];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MediaUploader: échec d'enregistrement de {Name}", session.OriginalName);
                result = new UploadFileResult(session.OriginalName, "error", null, ex.Message);
            }

            UploadStore.Sessions.TryRemove(session.Id, out _);
            UploadStore.TryDelete(session.TempPath);

            var saved = result.Status == "saved" ? 1 : 0;
            var scanQueued = false;
            if (saved > 0 && (session.Scan ?? config.AutoScan))
            {
                _libraryManager.QueueLibraryScan();
                scanQueued = true;
            }

            return new UploadResponse(saved, scanQueued, new[] { result });
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Abandonne un envoi et supprime le fichier temporaire.
    /// </summary>
    /// <param name="id">Identifiant de l'envoi.</param>
    /// <returns>204.</returns>
    [HttpDelete("Upload/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult AbortUpload(string id)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var session = FindSession(id, out var error);
        if (session is null)
        {
            return error!;
        }

        UploadStore.Sessions.TryRemove(session.Id, out _);
        UploadStore.TryDelete(session.TempPath);
        return NoContent();
    }

    private static Dictionary<string, ItemOverride> BuildOverrides(IEnumerable<string> ids, string? title, int? year, int? season, string? artist, string? album, bool? force)
    {
        var map = new Dictionary<string, ItemOverride>();
        if (string.IsNullOrWhiteSpace(title) && year is null && season is null && string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) && force != true)
        {
            return map;
        }

        var ov = new ItemOverride { Title = title, Year = year, Season = season, Artist = artist, Album = album, Force = force == true };
        foreach (var id in ids)
        {
            map[id] = ov;
        }

        return map;
    }

    private ChunkSession? FindSession(string id, out ActionResult? error)
    {
        error = null;
        if (!UploadStore.Sessions.TryGetValue(id, out var session))
        {
            error = NotFound(new { error = "Envoi inconnu ou expiré : recommencez." });
            return null;
        }

        if (session.Owner != CurrentOwner && !IsAdmin)
        {
            error = Forbid();
            return null;
        }

        return session;
    }
}
