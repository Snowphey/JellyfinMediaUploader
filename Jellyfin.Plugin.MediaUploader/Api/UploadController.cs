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
public record StatusResponse(
    bool MusicConfigured,
    bool MoviesConfigured,
    string AudioExtensions,
    string VideoExtensions,
    string ExtraExtensions,
    int MaxFileSizeMb,
    bool AutoScan,
    bool CanUpload);

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
            CanUpload);
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
        var maxBytes = config.MaxFileSizeMb > 0 ? config.MaxFileSizeMb * 1024L * 1024L : long.MaxValue;

        var results = new List<UploadFileResult>();
        var saved = 0;

        foreach (var file in request.Files)
        {
            var originalName = Path.GetFileName(file.FileName ?? string.Empty);
            var ext = Path.GetExtension(originalName).ToLowerInvariant();

            // Type : celui du formulaire, sinon déduit de l'extension.
            var type = forcedType ?? InferType(ext, audio, video);
            if (string.IsNullOrEmpty(originalName) || type is null)
            {
                results.Add(new UploadFileResult(originalName, "skipped", null, string.IsNullOrEmpty(originalName)
                    ? "Nom de fichier vide"
                    : $"Type indéterminé pour '{ext}' : précisez 'type' (music ou movie)"));
                continue;
            }

            var isMusic = type == "music";
            var root = isMusic ? config.MusicPath : config.MoviesPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                results.Add(new UploadFileResult(originalName, "error", null, $"Le dossier '{type}' n'est pas configuré dans les paramètres du plugin."));
                continue;
            }

            var allowed = (isMusic ? audio : video).Union(extra).ToHashSet();
            if (!allowed.Contains(ext))
            {
                results.Add(new UploadFileResult(originalName, "skipped", null, $"Extension non autorisée : '{ext}'"));
                continue;
            }

            if (file.Length > maxBytes)
            {
                results.Add(new UploadFileResult(originalName, "skipped", null, $"Fichier trop gros (max {config.MaxFileSizeMb} Mo)"));
                continue;
            }

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

                    var artist = structured ? Pick(request.Artist, tags?.Artist) : null;
                    var album = structured ? Pick(request.Album, tags?.Album) : null;
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
                        if (!string.IsNullOrWhiteSpace(request.Title))
                        {
                            title = request.Title.Trim();
                            year = request.Year;
                        }
                        else if (MediaNameParser.ParseMovie(originalName) is { } parsed)
                        {
                            title = parsed.Title;
                            year = request.Year ?? parsed.Year;
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
                    results.Add(new UploadFileResult(originalName, "error", null, "Chemin de destination invalide"));
                    continue;
                }

                if (System.IO.File.Exists(target) && !config.OverwriteExisting)
                {
                    target = UniquePath(target);
                    notes.Add("Nom déjà pris : enregistré sous un autre nom");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Move(tmp, target, overwrite: config.OverwriteExisting);
                saved++;

                if (config.ExtractCover && albumFolder is not null && tags?.CoverData is not null && tags.CoverExtension is not null)
                {
                    if (TryWriteCover(Path.GetDirectoryName(target)!, tags))
                    {
                        notes.Add("Pochette extraite des tags");
                    }
                }

                results.Add(new UploadFileResult(
                    originalName,
                    "saved",
                    target,
                    notes.Count > 0 ? string.Join(" · ", notes) : null,
                    Path.GetRelativePath(root, target)));
                _logger.LogInformation("MediaUploader: fichier enregistré {Path}", target);
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
