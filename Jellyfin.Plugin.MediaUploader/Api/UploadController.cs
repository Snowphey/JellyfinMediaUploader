using System.Collections.Concurrent;
using System.Net.Mime;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// Formulaire d'upload (multipart/form-data). Tous les champs sauf <c>files</c> sont optionnels.
/// </summary>
public class UploadRequest
{
    /// <summary>Gets or sets le type de média : "music" ou "movie". Vide = déduit de l'extension.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets l'artiste (remplace celui des tags).</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album (remplace celui des tags).</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets le titre du film (remplace celui déduit du nom de fichier).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année du film (remplace celle déduite du nom de fichier).</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets une valeur forçant ou désactivant le scan après upload (null = valeur de la config).</summary>
    public bool? Scan { get; set; }

    /// <summary>Gets or sets les fichiers envoyés.</summary>
    public List<IFormFile> Files { get; set; } = new();
}

/// <summary>
/// Résultat d'un fichier uploadé.
/// </summary>
/// <param name="Name">Nom d'origine.</param>
/// <param name="Status">"saved", "skipped" ou "error".</param>
/// <param name="Path">Chemin final (si enregistré).</param>
/// <param name="Message">Détail éventuel.</param>
/// <param name="Destination">Emplacement relatif au dossier de la bibliothèque (ex. « Artiste/Album/fichier.m4a »).</param>
public record UploadFileResult(string Name, string Status, string? Path, string? Message, string? Destination = null);

/// <summary>
/// Réponse de l'upload.
/// </summary>
/// <param name="Saved">Nombre de fichiers enregistrés.</param>
/// <param name="ScanQueued">Vrai si un scan de bibliothèque a été lancé.</param>
/// <param name="Files">Détail par fichier.</param>
public record UploadResponse(int Saved, bool ScanQueued, IReadOnlyList<UploadFileResult> Files);

/// <summary>
/// État exposé à l'interface.
/// </summary>
/// <param name="MusicConfigured">Dossier musique défini.</param>
/// <param name="MoviesConfigured">Dossier films défini.</param>
/// <param name="AudioExtensions">Extensions audio.</param>
/// <param name="VideoExtensions">Extensions vidéo.</param>
/// <param name="ExtraExtensions">Extensions annexes.</param>
/// <param name="MaxFileSizeMb">Taille max par fichier (Mo, 0 = illimitée).</param>
/// <param name="AutoScan">Scan automatique activé.</param>
/// <param name="CanUpload">L'utilisateur courant a le droit d'uploader.</param>
/// <param name="ChunkSizeMb">Taille des morceaux conseillée à l'interface (Mo).</param>
public record StatusResponse(
    bool MusicConfigured,
    bool MoviesConfigured,
    string AudioExtensions,
    string VideoExtensions,
    string ExtraExtensions,
    int MaxFileSizeMb,
    bool AutoScan,
    bool CanUpload,
    int ChunkSizeMb);

/// <summary>
/// Démarrage d'un envoi par morceaux.
/// </summary>
/// <param name="FileName">Nom du fichier.</param>
/// <param name="Size">Taille totale en octets.</param>
/// <param name="Type">"music" ou "movie" (vide = déduit de l'extension).</param>
/// <param name="Artist">Artiste (remplace celui des tags).</param>
/// <param name="Album">Album (remplace celui des tags).</param>
/// <param name="Title">Titre du film.</param>
/// <param name="Year">Année du film.</param>
/// <param name="Scan">Scan après envoi (null = valeur de la config).</param>
public record StartUploadRequest(string FileName, long Size, string? Type, string? Artist, string? Album, string? Title, int? Year, bool? Scan);

/// <summary>
/// Réponse au démarrage ou à la reprise d'un envoi par morceaux.
/// </summary>
/// <param name="UploadId">Identifiant de l'envoi.</param>
/// <param name="Received">Octets déjà reçus (prochain offset attendu).</param>
/// <param name="Size">Taille totale annoncée.</param>
/// <param name="ChunkSize">Taille de morceau conseillée en octets.</param>
public record ChunkUploadState(string UploadId, long Received, long Size, int ChunkSize);

/// <summary>
/// API d'upload. Authentification : session d'un utilisateur (tout le monde si autorisé dans les paramètres)
/// ou clé API Jellyfin (<c>X-Emby-Token: CLE</c> ou <c>Authorization: MediaBrowser Token="CLE"</c>).
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class UploadController : ControllerBase
{
    private static readonly HashSet<string> MovieOnlyExtras = new() { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup" };
    private static readonly HashSet<string> MusicOnlyExtras = new() { ".lrc" };

    private static readonly ConcurrentDictionary<string, ChunkSession> Sessions = new();
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

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

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private bool CanUpload => Config.AllowNonAdminUploads || User.IsInRole("Administrator");

    /// <summary>
    /// Page d'upload (ouverte directement ou intégrée dans un onglet par un plugin tiers).
    /// </summary>
    /// <returns>HTML.</returns>
    [HttpGet("Ui")]
    [AllowAnonymous]
    [Produces("text/html")]
    public ActionResult GetUi() => Resource("ui.html", "text/html; charset=utf-8");

    /// <summary>
    /// Retourne l'état de la configuration.
    /// </summary>
    /// <returns>État.</returns>
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var c = Config;
        return new StatusResponse(
            !string.IsNullOrWhiteSpace(c.MusicPath),
            !string.IsNullOrWhiteSpace(c.MoviesPath),
            c.AudioExtensions,
            c.VideoExtensions,
            c.ExtraExtensions,
            c.MaxFileSizeMb,
            c.AutoScan,
            CanUpload,
            ChunkMb(c));
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
    /// Envoie un ou plusieurs fichiers vers la bibliothèque musique ou films.
    /// Artiste/album (musique) et titre/année (film) sont déduits du fichier ; les champs du formulaire les remplacent si fournis.
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

        var forcedType = NormalizeType(request.Type);
        if (!string.IsNullOrWhiteSpace(request.Type) && forcedType is null)
        {
            return BadRequest(new { error = "Le champ 'type' doit valoir 'music' ou 'movie' (ou être omis)." });
        }

        var audio = ParseExtensions(config.AudioExtensions);
        var video = ParseExtensions(config.VideoExtensions);
        var extra = ParseExtensions(config.ExtraExtensions);

        var results = new List<UploadFileResult>();
        var saved = 0;

        foreach (var file in request.Files)
        {
            var originalName = Path.GetFileName(file.FileName ?? string.Empty);
            var ext = Path.GetExtension(originalName).ToLowerInvariant();

            var rejected = CheckFile(originalName, file.Length, forcedType, config, audio, video, extra, out var type);
            if (rejected is not null)
            {
                results.Add(rejected);
                continue;
            }

            var isMusic = type == "music";
            var root = isMusic ? config.MusicPath : config.MoviesPath;

            // Fichier temporaire dans le dossier de la bibliothèque (même disque : le déplacement final est instantané).
            // Le nom ne finit pas par une extension média, donc Jellyfin ne l'indexe pas pendant l'écriture.
            var tmp = Path.Combine(root, ".mu-" + Guid.NewGuid().ToString("N") + ".part");

            try
            {
                Directory.CreateDirectory(root);
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await file.CopyToAsync(output, HttpContext.RequestAborted).ConfigureAwait(false);
                }

                var placed = Place(tmp, root, isMusic, originalName, ext, request.Artist, request.Album, request.Title, request.Year, audio, config);
                if (placed.Status == "saved")
                {
                    saved++;
                }

                results.Add(placed);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                _logger.LogError(ex, "MediaUploader: échec d'enregistrement de {Name}", originalName);
                results.Add(new UploadFileResult(originalName, "error", null, ex.Message));
            }
        }

        var scan = request.Scan ?? config.AutoScan;
        var scanQueued = false;
        if (saved > 0 && scan)
        {
            _libraryManager.QueueLibraryScan();
            scanQueued = true;
        }

        return new UploadResponse(saved, scanQueued, results);
    }

    // Déduit le type, vérifie extension, configuration et taille. Retourne null si le fichier est accepté.
    private static UploadFileResult? CheckFile(
        string originalName,
        long length,
        string? forcedType,
        PluginConfiguration config,
        HashSet<string> audio,
        HashSet<string> video,
        HashSet<string> extra,
        out string? type)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        type = forcedType ?? InferType(ext, audio, video);
        if (string.IsNullOrEmpty(originalName) || type is null)
        {
            return new UploadFileResult(originalName, "skipped", null, string.IsNullOrEmpty(originalName)
                ? "Nom de fichier vide"
                : $"Type indéterminé pour '{ext}' : précisez 'type' (music ou movie)");
        }

        var isMusic = type == "music";
        var root = isMusic ? config.MusicPath : config.MoviesPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return new UploadFileResult(originalName, "error", null, $"Le dossier '{type}' n'est pas configuré dans les paramètres du plugin.");
        }

        var allowed = (isMusic ? audio : video).Union(extra).ToHashSet();
        if (!allowed.Contains(ext))
        {
            return new UploadFileResult(originalName, "skipped", null, $"Extension non autorisée : '{ext}'");
        }

        var maxBytes = config.MaxFileSizeMb > 0 ? config.MaxFileSizeMb * 1024L * 1024L : long.MaxValue;
        if (length > maxBytes)
        {
            return new UploadFileResult(originalName, "skipped", null, $"Fichier trop gros (max {config.MaxFileSizeMb} Mo)");
        }

        return null;
    }

    // Range un fichier temporaire complet à sa destination finale (tags, dossiers, nom unique, pochette).
    private UploadFileResult Place(
        string tmp,
        string root,
        bool isMusic,
        string originalName,
        string ext,
        string? artistOverride,
        string? albumOverride,
        string? titleOverride,
        int? yearOverride,
        HashSet<string> audio,
        PluginConfiguration config)
    {
        var layout = (isMusic ? config.MusicLayout : config.MoviesLayout) ?? "auto";
        var structured = !string.Equals(layout, "flat", StringComparison.OrdinalIgnoreCase);

        string target;
        AudioTags? tags = null;
        string? albumFolder = null;
        var notes = new List<string>();

        if (isMusic)
        {
            if (audio.Contains(ext))
            {
                tags = AudioTagReader.Read(tmp, ext);
            }

            var artist = structured ? Pick(artistOverride, tags?.Artist) : null;
            var album = structured ? Pick(albumOverride, tags?.Album) : null;
            target = PathBuilder.Music(root, artist, album, originalName);
            albumFolder = album;

            if (structured && artist is null && album is null && audio.Contains(ext))
            {
                notes.Add(tags is null ? "Tags illisibles : placé à la racine" : "Pas d'artiste ni d'album dans les tags : placé à la racine");
            }
        }
        else
        {
            string? title = null;
            int? year = null;
            if (structured)
            {
                if (!string.IsNullOrWhiteSpace(titleOverride))
                {
                    title = titleOverride.Trim();
                    year = yearOverride;
                }
                else if (MediaNameParser.ParseMovie(originalName) is { } parsed)
                {
                    title = parsed.Title;
                    year = yearOverride ?? parsed.Year;
                }
            }

            if (title is null)
            {
                target = Path.Combine(root, PathBuilder.SanitizeSegment(originalName, "file"));
                if (structured)
                {
                    notes.Add("Titre/année non reconnus dans le nom : placé à la racine");
                }
            }
            else
            {
                target = PathBuilder.Movie(root, title, year, originalName);
            }
        }

        if (!PathBuilder.IsInside(root, target))
        {
            TryDelete(tmp);
            return new UploadFileResult(originalName, "error", null, "Chemin de destination invalide");
        }

        if (System.IO.File.Exists(target) && !config.OverwriteExisting)
        {
            target = UniquePath(target);
            notes.Add("Nom déjà pris : enregistré sous un autre nom");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        System.IO.File.Move(tmp, target, overwrite: config.OverwriteExisting);

        if (config.ExtractCover && albumFolder is not null && tags?.CoverData is not null && tags.CoverExtension is not null)
        {
            if (TryWriteCover(Path.GetDirectoryName(target)!, tags))
            {
                notes.Add("Pochette extraite des tags");
            }
        }

        _logger.LogInformation("MediaUploader: fichier enregistré {Path}", target);
        return new UploadFileResult(
            originalName,
            "saved",
            target,
            notes.Count > 0 ? string.Join(" · ", notes) : null,
            Path.GetRelativePath(root, target));
    }

    private static int ChunkMb(PluginConfiguration c) => Math.Clamp(c.ChunkSizeMb <= 0 ? 8 : c.ChunkSizeMb, 1, 90);

    private string CurrentOwner =>
        User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst("Jellyfin-Token")?.Value ?? string.Empty;

    private ChunkSession? FindSession(string id, out ActionResult? error)
    {
        error = null;
        if (!Sessions.TryGetValue(id, out var session))
        {
            error = NotFound(new { error = "Envoi inconnu ou expiré : recommencez." });
            return null;
        }

        if (session.Owner != CurrentOwner && !User.IsInRole("Administrator"))
        {
            error = Forbid();
            return null;
        }

        return session;
    }

    // Supprime les envois abandonnés et les .part orphelins (plus de 24 h) des dossiers de bibliothèque.
    private void CleanupStale(PluginConfiguration config)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, session) in Sessions)
        {
            if (now - session.LastActivity > SessionTimeout && Sessions.TryRemove(id, out var removed))
            {
                TryDelete(removed.TempPath);
            }
        }

        var active = Sessions.Values.Select(s => s.TempPath).ToHashSet();
        foreach (var root in new[] { config.MusicPath, config.MoviesPath })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, ".mu-*.part"))
                {
                    if (!active.Contains(file) && now - System.IO.File.GetLastWriteTimeUtc(file) > OrphanAge)
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
    /// Démarre un envoi par morceaux (gros fichiers, proxys limitant la taille des requêtes).
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
        var forcedType = NormalizeType(request.Type);
        if (!string.IsNullOrWhiteSpace(request.Type) && forcedType is null)
        {
            return BadRequest(new { error = "Le champ 'type' doit valoir 'music' ou 'movie' (ou être omis)." });
        }

        if (request.Size < 0)
        {
            return BadRequest(new { error = "Taille invalide." });
        }

        var originalName = Path.GetFileName(request.FileName ?? string.Empty);
        var rejected = CheckFile(
            originalName,
            request.Size,
            forcedType,
            config,
            ParseExtensions(config.AudioExtensions),
            ParseExtensions(config.VideoExtensions),
            ParseExtensions(config.ExtraExtensions),
            out var type);
        if (rejected is not null)
        {
            return BadRequest(new { error = rejected.Message, status = rejected.Status });
        }

        var root = type == "music" ? config.MusicPath : config.MoviesPath;
        CleanupStale(config);

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

        Sessions[id] = new ChunkSession
        {
            Id = id,
            Owner = CurrentOwner,
            TempPath = tmp,
            Root = root,
            IsMusic = type == "music",
            OriginalName = originalName,
            Size = request.Size,
            Artist = request.Artist,
            Album = request.Album,
            Title = request.Title,
            Year = request.Year,
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

            // Un morceau précédent interrompu a pu laisser des octets en trop : on repart de la position validée.
            await using var output = new FileStream(session.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            output.SetLength(session.Received);
            output.Seek(session.Received, SeekOrigin.Begin);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (session.Received + written > session.Size)
                {
                    output.SetLength(session.Received);
                    return BadRequest(new { error = "Le morceau dépasse la taille annoncée.", received = session.Received });
                }

                await output.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted).ConfigureAwait(false);
            }

            await output.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            session.Received += written;
            session.LastActivity = DateTime.UtcNow;
            return Ok(new { received = session.Received });
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Termine l'envoi : vérifie la taille, range le fichier (tags, dossiers…) et lance éventuellement un scan.
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
                var ext = Path.GetExtension(session.OriginalName).ToLowerInvariant();
                result = Place(
                    session.TempPath,
                    session.Root,
                    session.IsMusic,
                    session.OriginalName,
                    ext,
                    session.Artist,
                    session.Album,
                    session.Title,
                    session.Year,
                    ParseExtensions(config.AudioExtensions),
                    config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MediaUploader: échec d'enregistrement de {Name}", session.OriginalName);
                result = new UploadFileResult(session.OriginalName, "error", null, ex.Message);
            }

            Sessions.TryRemove(session.Id, out _);
            TryDelete(session.TempPath);

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

        Sessions.TryRemove(session.Id, out _);
        TryDelete(session.TempPath);
        return NoContent();
    }

    private ContentResult Resource(string name, string contentType)
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}");
        if (stream is null)
        {
            return new ContentResult { StatusCode = StatusCodes.Status404NotFound, Content = "Ressource introuvable" };
        }

        using var reader = new StreamReader(stream);
        Response.Headers["Cache-Control"] = "no-cache";
        return new ContentResult { Content = reader.ReadToEnd(), ContentType = contentType, StatusCode = StatusCodes.Status200OK };
    }

    private static string? NormalizeType(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "music" => "music",
            "movie" or "movies" => "movie",
            _ => null
        };
    }

    private static string? InferType(string ext, HashSet<string> audio, HashSet<string> video)
    {
        if (audio.Contains(ext) || MusicOnlyExtras.Contains(ext))
        {
            return "music";
        }

        if (video.Contains(ext) || MovieOnlyExtras.Contains(ext))
        {
            return "movie";
        }

        return null;
    }

    private static string? Pick(string? explicitValue, string? fromFile)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return explicitValue.Trim();
        }

        return fromFile;
    }

    // Écrit cover.jpg/png/webp dans le dossier de l'album si aucune pochette n'y est déjà.
    private bool TryWriteCover(string folder, AudioTags tags)
    {
        try
        {
            var hasCover = Directory.EnumerateFiles(folder)
                .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                .Any(n => n is "cover" or "folder" or "poster");
            if (hasCover)
            {
                return false;
            }

            System.IO.File.WriteAllBytes(Path.Combine(folder, "cover" + tags.CoverExtension), tags.CoverData!);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaUploader: pochette non extraite dans {Folder}", folder);
            return false;
        }
    }

    private static HashSet<string> ParseExtensions(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nettoyage best effort.
        }
    }
}
