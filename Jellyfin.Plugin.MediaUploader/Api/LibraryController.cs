using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaUploader.Services;
using Jellyfin.Plugin.MediaUploader.Services.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>Déplacement ou renommage d'éléments d'une bibliothèque.</summary>
public class LibraryMoveRequest
{
    /// <summary>Gets or sets la bibliothèque : "music", "movies" ou "shows".</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets les éléments à déplacer (chemins relatifs à la bibliothèque).</summary>
    public List<string>? Items { get; set; }

    /// <summary>Gets or sets le dossier de destination (relatif ; vide : la racine).</summary>
    public string? Destination { get; set; }

    /// <summary>Gets or sets le nouveau nom (renommage d'un seul élément).</summary>
    public string? NewName { get; set; }

    /// <summary>Gets or sets la politique en cas de fichier déjà présent : "skip", "rename" ou "overwrite" (administrateurs).</summary>
    public string? Policy { get; set; }

    /// <summary>Gets or sets a value indicating whether seul l'aperçu est demandé.</summary>
    public bool DryRun { get; set; }
}

/// <summary>Suppression d'éléments d'une bibliothèque.</summary>
public class LibraryDeleteRequest
{
    /// <summary>Gets or sets la bibliothèque.</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets les éléments à supprimer (chemins relatifs).</summary>
    public List<string>? Items { get; set; }

    /// <summary>Gets or sets a value indicating whether seul l'aperçu est demandé.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets le mot de confirmation (« SUPPRIMER ») exigé au-delà du seuil réglé.</summary>
    public string? Confirm { get; set; }
}

/// <summary>Création d'un dossier.</summary>
public class LibraryFolderRequest
{
    /// <summary>Gets or sets la bibliothèque.</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets le dossier parent (relatif).</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets le nom du nouveau dossier.</summary>
    public string? Name { get; set; }
}

/// <summary>Restauration ou purge d'éléments de la corbeille.</summary>
public class LibraryTrashRequest
{
    /// <summary>Gets or sets la bibliothèque.</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets les identifiants d'éléments de la corbeille.</summary>
    public List<string>? Ids { get; set; }

    /// <summary>Gets or sets a value indicating whether toute la corbeille de cette bibliothèque est concernée.</summary>
    public bool All { get; set; }
}

/// <summary>Ajout d'une pochette à un dossier d'album.</summary>
public class LibraryCoverRequest
{
    /// <summary>Gets or sets la bibliothèque (musique).</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets le dossier de l'album (relatif).</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets la source choisie : "musicbrainz" ou "itunes" (ignorée si une image est envoyée).</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets l'identifiant chez la source (MBID pour "musicbrainz").</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets l'adresse de l'image (source "itunes").</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets une image envoyée par l'utilisateur (JPEG ou PNG, en base64).</summary>
    public string? ImageBase64 { get; set; }

    /// <summary>Gets or sets a value indicating whether l'image est enregistrée dans le dossier (cover.jpg ou cover.png).</summary>
    public bool? SaveFile { get; set; }

    /// <summary>Gets or sets a value indicating whether l'image est intégrée aux morceaux.</summary>
    public bool? Embed { get; set; }

    /// <summary>Gets or sets a value indicating whether une pochette déjà présente est remplacée.</summary>
    public bool Overwrite { get; set; }
}

/// <summary>Lecture ou modification des tags des morceaux d'éléments (fichiers ou dossiers entiers).</summary>
public class LibraryTagsRequest
{
    /// <summary>Gets or sets la bibliothèque (musique).</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets les éléments : morceaux, ou dossiers dont tous les morceaux sont concernés.</summary>
    public List<string>? Items { get; set; }

    /// <summary>Gets or sets les modifications (un champ absent n'est pas touché, une chaîne vide l'efface).</summary>
    public TagChanges? Changes { get; set; }

    /// <summary>Gets or sets a value indicating whether les fichiers sont ensuite rangés dans le dossier « Artiste/Album » correspondant à leurs nouveaux tags.</summary>
    public bool Reorganize { get; set; }

    /// <summary>Gets or sets la politique en cas de fichier déjà présent au rangement : "skip" ou "rename".</summary>
    public string? Policy { get; set; }

    /// <summary>Gets or sets a value indicating whether seul l'aperçu est demandé.</summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// Gestion des fichiers des bibliothèques (parcours, tailles, lecture, téléchargement, déplacement, tags, suppression avec corbeille, santé) pour la page « Bibliothèque ».
/// Chaque action importante est écrite dans le journal d'activité de Jellyfin, et Jellyfin est prévenu des changements.
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class LibraryController : MediaUploaderControllerBase
{
    private const string ConfirmWord = "SUPPRIMER";
    private const int MaxTagFiles = 2000;
    private const int MaxActivityLines = 600;

    private static readonly ConcurrentDictionary<string, (DateTime At, LibraryStats Stats)> SizeCache = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IFileSystem _fileSystem;
    private readonly IUserManager _userManager;
    private readonly ILogger<LibraryController> _logger;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="LibraryController"/>.
    /// </summary>
    /// <param name="libraryManager">Gestionnaire de bibliothèque.</param>
    /// <param name="providerManager">Gestionnaire de métadonnées (actualisation ciblée d'une bibliothèque après un changement).</param>
    /// <param name="fileSystem">Système de fichiers de Jellyfin.</param>
    /// <param name="playlistManager">Gestionnaire de listes de lecture (les morceaux déplacés y sont remis à leur place).</param>
    /// <param name="userManager">Gestionnaire d'utilisateurs (droit de suppression de Jellyfin).</param>
    /// <param name="activityManager">Journal d'activité de Jellyfin.</param>
    /// <param name="logger">Logger.</param>
    public LibraryController(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IPlaylistManager playlistManager, IUserManager userManager, IActivityManager activityManager, ILogger<LibraryController> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _playlistManager = playlistManager;
        _fileSystem = fileSystem;
        _userManager = userManager;
        _logger = logger;
        PluginActivity.Attach(activityManager, logger);
    }

    private bool CanView => Config.LibraryEnabled && (IsAdmin || (Config.LibraryViewAccess == "uploaders" && CanUpload));

    private bool CanMove => CanView && (IsAdmin || Config.LibraryMoveAccess == "viewers");

    private bool CanDelete => CanView && Config.LibraryDeleteAccess switch
    {
        "admins" => IsAdmin,
        "jellyfin" => IsAdmin || UserCanDeleteContent(),
        _ => false
    };

    private int MaxItems => Math.Clamp(Config.LibraryMaxItems <= 0 ? 500 : Config.LibraryMaxItems, 1, 5000);

    /// <summary>
    /// Page « Bibliothèque » (ouverte directement ou intégrée dans un onglet par un plugin tiers). L'accès aux données est vérifié par chaque route de l'API.
    /// </summary>
    /// <returns>HTML.</returns>
    [HttpGet("LibraryUi")]
    [AllowAnonymous]
    [Produces("text/html")]
    public ActionResult GetUi()
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.library.html");
        if (stream is null)
        {
            return new ContentResult { StatusCode = StatusCodes.Status404NotFound, Content = "Ressource introuvable" };
        }

        using var reader = new StreamReader(stream);
        Response.Headers["Cache-Control"] = "no-cache";
        return new ContentResult { Content = reader.ReadToEnd(), ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status200OK };
    }

    /// <summary>
    /// Ce que l'utilisateur courant peut faire, et les réglages utiles à l'interface.
    /// </summary>
    /// <returns>Droits et bibliothèques.</returns>
    [HttpGet("Library/Capabilities")]
    public ActionResult GetCapabilities()
    {
        var c = Config;
        var canView = CanView;
        return Ok(new
        {
            c.LibraryEnabled,
            CanView = canView,
            CanMove = canView && CanMove,
            CanDelete = canView && CanDelete,
            CanPurge = canView && IsAdmin,
            IsAdmin,
            DeleteMode = c.LibraryDeleteMode == "permanent" ? "permanent" : "trash",
            TrashDays = c.LibraryTrashDays,
            ConfirmThreshold = c.LibraryConfirmThreshold,
            ConfirmWord,
            MaxItems,
            Roots = Roots().Where(_ => canView).Select(r => new { r.Key, r.Label }).ToList()
        });
    }

    /// <summary>
    /// Contenu d'un dossier : sous-dossiers et fichiers, avec pour les médias l'état dans Jellyfin (analysé ou non) et son identifiant.
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="path">Dossier (relatif ; vide : la racine).</param>
    /// <returns>Éléments.</returns>
    [HttpGet("Library/List")]
    public ActionResult List([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        if (!LibraryFs.TryList(r!, path, KindOf(), out var entries, out var error))
        {
            return BadRequest(new { error });
        }

        LibraryFs.TryResolve(r!, path, out var dirFull, out _);
        var budget = 300;
        var items = entries.Select(e =>
        {
            Guid? itemId = null;
            bool? indexed = null;
            if (e.Kind is "audio" or "video" && budget-- > 0)
            {
                var item = _libraryManager.FindByPath(Path.Combine(dirFull, e.Name), false);
                indexed = item is not null;
                itemId = item?.Id;
            }

            return new { e.Name, e.IsDir, e.Size, Modified = e.ModifiedUtc, e.Children, e.Kind, e.Hidden, Indexed = indexed, ItemId = itemId };
        }).ToList();
        return Ok(new { Path = LibraryFs.Relative(r!, dirFull), Entries = items });
    }

    /// <summary>
    /// Poids d'un élément (nombre de fichiers, dossiers, octets). Gardé quelques minutes : la page l'appelle pour chaque dossier affiché.
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="path">Élément (relatif).</param>
    /// <returns>Totaux.</returns>
    [HttpGet("Library/Size")]
    public ActionResult Size([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        if (!LibraryFs.TryResolve(r!, path, out var full, out var error))
        {
            return BadRequest(new { error });
        }

        var key = r!.Key + "|" + full;
        if (SizeCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
        {
            return Ok(hit.Stats);
        }

        var stats = LibraryFs.Stats(full, 200_000);
        SizeCache[key] = (DateTime.UtcNow, stats);
        return Ok(stats);
    }

    /// <summary>
    /// Recherche par nom dans toute une bibliothèque.
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="q">Texte cherché.</param>
    /// <returns>Résultats (200 au plus).</returns>
    [HttpGet("Library/Search")]
    public ActionResult Search([FromQuery] string? root, [FromQuery] string? q)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return BadRequest(new { error = "Saisissez au moins 2 caractères." });
        }

        var needle = q.Trim();
        var kindOf = KindOf();
        var hits = new List<object>();
        var seen = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var info in new DirectoryInfo(r!.Path).EnumerateFileSystemInfos("*", options))
        {
            if (++seen > 300_000 || hits.Count >= 200)
            {
                break;
            }

            var relative = LibraryFs.Relative(r, info.FullName);
            if (relative.StartsWith(LibraryFs.TrashDirName, StringComparison.Ordinal) || info.Name.StartsWith(".mu-", StringComparison.Ordinal))
            {
                continue;
            }

            if (info.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                var isDir = info is DirectoryInfo;
                hits.Add(new { Path = relative, info.Name, IsDir = isDir, Size = isDir ? 0 : ((FileInfo)info).Length, Kind = isDir ? "folder" : kindOf(info.Extension.ToLowerInvariant()) });
            }
        }

        return Ok(new { Hits = hits, Truncated = hits.Count >= 200 });
    }

    /// <summary>
    /// Sert un fichier de la bibliothèque (lecture dans le navigateur, avec accès par plages, ou téléchargement avec <c>download=true</c>).
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="path">Fichier (relatif).</param>
    /// <param name="download">Vrai pour forcer le téléchargement.</param>
    /// <returns>Contenu du fichier.</returns>
    [HttpGet("Library/File")]
    public ActionResult GetFile([FromQuery] string? root, [FromQuery] string? path, [FromQuery] bool download = false)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        if (!LibraryFs.TryResolve(r!, path, out var full, out var error))
        {
            return BadRequest(new { error });
        }

        if (!System.IO.File.Exists(full) || LibraryFs.HasLink(r!, full))
        {
            return NotFound(new { error = "Fichier introuvable." });
        }

        var name = Path.GetFileName(full);
        return PhysicalFile(full, MimeOf(Path.GetExtension(full)), download ? name : null, enableRangeProcessing: true);
    }

    /// <summary>
    /// Pochette intégrée à un fichier audio.
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="path">Fichier audio (relatif).</param>
    /// <returns>Image.</returns>
    [HttpGet("Library/Cover")]
    public ActionResult GetCover([FromQuery] string? root, [FromQuery] string? path)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        if (!LibraryFs.TryResolve(r!, path, out var full, out _) || !System.IO.File.Exists(full) || LibraryFs.HasLink(r!, full))
        {
            return NotFound(new { error = "Fichier introuvable." });
        }

        var details = AudioTagReader.ReadDetails(full, Path.GetExtension(full).ToLowerInvariant());
        if (details?.CoverData is null || details.CoverMime is null)
        {
            return NotFound(new { error = "Pas de pochette intégrée." });
        }

        Response.Headers["Cache-Control"] = "private, max-age=300";
        return File(details.CoverData, details.CoverMime);
    }

    /// <summary>
    /// Télécharge un ou plusieurs éléments : le fichier tel quel, ou une archive zip (dossiers et sélections multiples, sans recompression).
    /// </summary>
    /// <param name="root">Bibliothèque.</param>
    /// <param name="path">Éléments (relatifs), le paramètre peut être répété.</param>
    /// <returns>Fichier ou archive.</returns>
    [HttpGet("Library/Download")]
    public async Task<ActionResult> Download([FromQuery] string? root, [FromQuery] string[]? path)
    {
        if (!Gate(root, out var r, out var denied))
        {
            return denied!;
        }

        var items = Clean(path);
        if (items.Count == 0 || items.Count > MaxItems)
        {
            return BadRequest(new { error = $"Choisissez de 1 à {MaxItems} éléments." });
        }

        var resolved = new List<(string Relative, string Full)>();
        foreach (var item in items)
        {
            if (!LibraryFs.TryResolve(r!, item, out var full, out var error) || LibraryFs.HasLink(r!, full) || (!System.IO.File.Exists(full) && !Directory.Exists(full)))
            {
                return BadRequest(new { error = error ?? $"Élément introuvable : {item}" });
            }

            resolved.Add((item, full));
        }

        // Un seul fichier : servi tel quel.
        if (resolved.Count == 1 && System.IO.File.Exists(resolved[0].Full))
        {
            return PhysicalFile(resolved[0].Full, MimeOf(Path.GetExtension(resolved[0].Full)), Path.GetFileName(resolved[0].Full), enableRangeProcessing: true);
        }

        var archive = resolved.Count == 1 ? Path.GetFileName(resolved[0].Full) : r!.Label.Replace(' ', '-').ToLowerInvariant() + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var stats = resolved.Select(x => LibraryFs.Stats(x.Full)).ToList();
        PluginActivity.Log(
            $"{CurrentUserName} a téléchargé {resolved.Count} élément{(resolved.Count > 1 ? "s" : string.Empty)} de la bibliothèque ({r!.Label})",
            "MediaUploaderLibrary",
            CurrentUserId,
            $"{stats.Sum(s => s.Files)} fichier(s), {ActivityText.Size(stats.Sum(s => s.Bytes))}",
            ActivityText.LibraryDetail(items, MaxActivityLines));

        Response.ContentType = "application/zip";
        Response.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(archive + ".zip");
        var body = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (body is not null)
        {
            body.AllowSynchronousIO = true;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        using (var zip = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = 0;
            foreach (var (relative, full) in resolved)
            {
                var files = System.IO.File.Exists(full)
                    ? new[] { (full, Path.GetFileName(full)) }
                    : Directory.EnumerateFiles(full, "*", options).Select(f => (f, Path.GetFileName(full) + "/" + Path.GetRelativePath(full, f).Replace('\\', '/'))).ToArray();
                foreach (var (file, entryName) in files)
                {
                    if (++entries > 200_000 || Path.GetFileName(file).StartsWith(".mu-", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Les médias sont déjà compressés : on les stocke tels quels (pas de recompression, donc pas de charge processeur).
                    var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using var target = entry.Open();
                    await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                    await source.CopyToAsync(target, HttpContext.RequestAborted).ConfigureAwait(false);
                }
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Crée un dossier.
    /// </summary>
    /// <param name="request">Bibliothèque, dossier parent et nom.</param>
    /// <returns>Chemin du dossier créé.</returns>
    [HttpPost("Library/Folder")]
    public ActionResult CreateFolder([FromBody] LibraryFolderRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied) || !CanMove)
        {
            return denied ?? Forbid();
        }

        if (!LibraryFs.TryCreateFolder(r!, request.Parent, request.Name, out var created, out var error))
        {
            return BadRequest(new { error });
        }

        SizeCache.Clear();
        return Ok(new { Path = created });
    }

    /// <summary>
    /// Déplace ou renomme des éléments (ou en donne l'aperçu : conflits, fusions).
    /// </summary>
    /// <param name="request">Éléments, destination, nouveau nom, politique de conflit.</param>
    /// <returns>Résultat par élément.</returns>
    [HttpPost("Library/Move")]
    public ActionResult Move([FromBody] LibraryMoveRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!CanMove)
        {
            return Forbid();
        }

        var items = Clean(request.Items);
        if (items.Count == 0 || items.Count > MaxItems)
        {
            return BadRequest(new { error = $"Choisissez de 1 à {MaxItems} éléments." });
        }

        // Écraser des fichiers est réservé aux administrateurs.
        var policy = request.Policy is "rename" or "skip" ? request.Policy : request.Policy == "overwrite" && IsAdmin ? "overwrite" : "skip";
        var captured = request.DryRun ? new Dictionary<Guid, string>() : CaptureItems(r!, items);
        var results = LibraryFs.Move(r!, items, request.Destination, request.NewName, policy, request.DryRun, IsBusy);
        if (!request.DryRun)
        {
            var done = results.Where(x => x.Status is "moved" or "merged" or "renamed").ToList();
            if (done.Count > 0)
            {
                SizeCache.Clear();
                var prefixes = done.Where(x => x.Target is not null).Select(x => (From: Full(r!, x.Source), To: Full(r!, x.Target!))).ToList();
                Relink(captured, old => MapPrefix(prefixes, old));
                string name;
                if (!string.IsNullOrWhiteSpace(request.NewName) && items.Count == 1)
                {
                    name = $"{CurrentUserName} a renommé « {items[0]} » en « {done[0].Target} » ({r!.Label})";
                }
                else
                {
                    var dest = string.IsNullOrWhiteSpace(request.Destination) ? "la racine" : "« " + request.Destination.Trim('/') + " »";
                    name = $"{CurrentUserName} a déplacé {done.Count} élément{(done.Count > 1 ? "s" : string.Empty)} vers {dest} ({r!.Label})";
                }

                var lines = new List<string>();
                foreach (var x in done)
                {
                    lines.Add($"{x.Source} → {x.Target}" + (x.Message is null ? string.Empty : $" ({x.Message})"));
                    foreach (var f in x.Listing ?? Array.Empty<LibraryFile>())
                    {
                        lines.Add($"    {x.Source}/{f.Path} → {x.Target}/{f.Path} ({ActivityText.Size(f.Size)})");
                    }
                }

                PluginActivity.Log(name, "MediaUploaderLibrary", CurrentUserId, $"{done.Sum(x => x.Files)} fichier(s), {ActivityText.Size(done.Sum(x => x.Bytes))}", ActivityText.LibraryDetail(lines, MaxActivityLines));
                AfterChange(r!, items, done.Where(x => x.Target is not null).Select(x => x.Target!));
            }
        }

        return Ok(new { Policy = policy, Results = results });
    }

    /// <summary>
    /// Supprime des éléments (ou en donne l'aperçu : arborescence, nombre de fichiers, taille). Mis à la corbeille ou définitif selon le réglage.
    /// </summary>
    /// <param name="request">Éléments et mot de confirmation.</param>
    /// <returns>Résultat par élément.</returns>
    [HttpPost("Library/Delete")]
    public ActionResult Delete([FromBody] LibraryDeleteRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!CanDelete)
        {
            return Forbid();
        }

        var items = Clean(request.Items);
        if (items.Count == 0 || items.Count > MaxItems)
        {
            return BadRequest(new { error = $"Choisissez de 1 à {MaxItems} éléments." });
        }

        var mode = Config.LibraryDeleteMode == "permanent" ? "permanent" : "trash";
        var preview = LibraryFs.Delete(r!, items, mode, CurrentUserName, dryRun: true, IsBusy, DateTime.UtcNow);
        var okPreview = preview.Where(x => x.Status == "ok").ToList();
        var totalFiles = okPreview.Sum(x => x.Files);
        var needsConfirm = Config.LibraryConfirmThreshold <= 0 || totalFiles >= Config.LibraryConfirmThreshold;
        if (request.DryRun)
        {
            return Ok(new { Mode = mode, NeedsConfirm = needsConfirm, ConfirmWord, TotalFiles = totalFiles, TotalBytes = okPreview.Sum(x => x.Bytes), Results = preview });
        }

        if (needsConfirm && !string.Equals(request.Confirm?.Trim(), ConfirmWord, StringComparison.Ordinal))
        {
            return BadRequest(new { error = $"Confirmation requise : tapez {ConfirmWord}.", needsConfirm = true });
        }

        var results = LibraryFs.Delete(r!, items, mode, CurrentUserName, dryRun: false, IsBusy, DateTime.UtcNow);
        var done = results.Where(x => x.Status is "trashed" or "deleted").ToList();
        if (done.Count > 0)
        {
            SizeCache.Clear();
            var folders = done.Count(x => x.IsDir);
            var files = done.Sum(x => x.Files);
            var how = mode == "permanent" ? "définitivement" : "vers la corbeille";
            var name = $"{CurrentUserName} a supprimé {ActivityText.Count(folders, files)} ({r!.Label}, {how})";
            var lines = new List<string>();
            foreach (var x in done)
            {
                lines.Add(x.IsDir ? $"{x.Source}/ : dossier, {x.Files} fichier(s), {ActivityText.Size(x.Bytes)}" : $"{x.Source} ({ActivityText.Size(x.Bytes)})");
                if (x.IsDir)
                {
                    foreach (var f in x.Listing ?? Array.Empty<LibraryFile>())
                    {
                        lines.Add($"    {x.Source}/{f.Path} ({ActivityText.Size(f.Size)})");
                    }

                    if (x.ListingTruncated)
                    {
                        lines.Add($"    … liste tronquée ({x.Files} fichiers au total)");
                    }
                }
            }

            PluginActivity.Log(name, "MediaUploaderLibrary", CurrentUserId, $"{files} fichier(s), {ActivityText.Size(done.Sum(x => x.Bytes))}", ActivityText.LibraryDetail(lines, MaxActivityLines), mode == "permanent" ? LogLevel.Warning : LogLevel.Information);
            AfterChange(r, items.Where(i => done.Any(d => d.Source == i)), Array.Empty<string>());
        }

        return Ok(new { Mode = mode, Results = results });
    }

    /// <summary>
    /// Contenu de la corbeille de toutes les bibliothèques.
    /// </summary>
    /// <returns>Éléments, les plus récents d'abord.</returns>
    [HttpGet("Library/Trash")]
    public ActionResult GetTrash()
    {
        if (!CanView)
        {
            return Forbid();
        }

        var days = Config.LibraryTrashDays;
        var now = DateTime.UtcNow;
        var entries = Roots().SelectMany(r => LibraryFs.ListTrash(r)).OrderByDescending(e => e.DeletedAt).Select(e => new
        {
            e.Id,
            e.Root,
            e.OriginalPath,
            e.IsDir,
            e.Files,
            e.Bytes,
            e.DeletedAt,
            e.DeletedBy,
            DaysLeft = days > 0 ? Math.Max(0, (int)Math.Ceiling(days - (now - e.DeletedAt).TotalDays)) : (int?)null
        }).ToList();
        return Ok(new { Days = days, Entries = entries });
    }

    /// <summary>
    /// Remet des éléments de la corbeille à leur place.
    /// </summary>
    /// <param name="request">Bibliothèque et identifiants.</param>
    /// <returns>Résultat par élément.</returns>
    [HttpPost("Library/Trash/Restore")]
    public ActionResult Restore([FromBody] LibraryTrashRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!CanDelete)
        {
            return Forbid();
        }

        var ids = request.All ? LibraryFs.ListTrash(r!).Select(e => e.Id).ToList() : Clean(request.Ids);
        if (ids.Count == 0 || ids.Count > MaxItems)
        {
            return BadRequest(new { error = $"Choisissez de 1 à {MaxItems} éléments." });
        }

        var results = LibraryFs.Restore(r!, ids);
        var done = results.Where(x => x.Status is "moved" or "renamed").ToList();
        if (done.Count > 0)
        {
            SizeCache.Clear();
            PluginActivity.Log(
                $"{CurrentUserName} a restauré {done.Count} élément{(done.Count > 1 ? "s" : string.Empty)} depuis la corbeille ({r!.Label})",
                "MediaUploaderLibrary",
                CurrentUserId,
                $"{done.Sum(x => x.Files)} fichier(s), {ActivityText.Size(done.Sum(x => x.Bytes))}",
                ActivityText.LibraryDetail(done.Select(x => x.Source + (x.Status == "renamed" ? $" (restauré sous « {x.Target} »)" : string.Empty)).ToList(), MaxActivityLines));
            AfterChange(r, Array.Empty<string>(), done.Where(x => x.Target is not null).Select(x => x.Target!));
        }

        return Ok(new { Results = results });
    }

    /// <summary>
    /// Supprime définitivement des éléments de la corbeille (administrateurs).
    /// </summary>
    /// <param name="request">Bibliothèque et identifiants (ou toute la corbeille).</param>
    /// <returns>Éléments purgés.</returns>
    [HttpPost("Library/Trash/Purge")]
    public ActionResult Purge([FromBody] LibraryTrashRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!IsAdmin)
        {
            return Forbid();
        }

        var ids = request.All ? LibraryFs.ListTrash(r!).Select(e => e.Id).ToList() : Clean(request.Ids);
        var purged = LibraryFs.Purge(r!, ids);
        if (purged.Count > 0)
        {
            PluginActivity.Log(
                $"{CurrentUserName} a vidé {(request.All ? "la corbeille" : purged.Count + " élément" + (purged.Count > 1 ? "s" : string.Empty) + " de la corbeille")} ({r!.Label}) : suppression définitive",
                "MediaUploaderLibrary",
                CurrentUserId,
                $"{purged.Sum(x => x.Files)} fichier(s), {ActivityText.Size(purged.Sum(x => x.Bytes))}",
                ActivityText.LibraryDetail(purged.Select(x => $"{x.OriginalPath} ({x.Files} fichier(s), supprimé par {x.DeletedBy})").ToList(), MaxActivityLines),
                LogLevel.Warning);
        }

        return Ok(new { Purged = purged.Count });
    }

    /// <summary>
    /// Anomalies de toutes les bibliothèques : dossiers vides, fichiers partiels abandonnés, morceaux sans pochette, albums en double, fichiers inconnus.
    /// </summary>
    /// <returns>Un rapport par bibliothèque.</returns>
    [HttpGet("Library/Health")]
    public ActionResult Health()
    {
        if (!CanView)
        {
            return Forbid();
        }

        var kindOf = KindOf();
        var reports = Roots().Select(r => new
        {
            r.Key,
            r.Label,
            Report = LibraryFs.Health(r, kindOf, IsBusy, DateTime.UtcNow, r.Key == "music", r.Key == "music" ? LibraryTags.HasCover : null)
        }).ToList();
        return Ok(new { Reports = reports });
    }

    /// <summary>
    /// Tags des morceaux d'éléments (un morceau, ou tous ceux d'un dossier) : valeurs distinctes par champ, pour préremplir l'éditeur.
    /// </summary>
    /// <param name="request">Bibliothèque et éléments.</param>
    /// <returns>Valeurs par champ.</returns>
    [HttpPost("Library/Tags/Read")]
    public ActionResult ReadTags([FromBody] LibraryTagsRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (r!.Key != "music")
        {
            return BadRequest(new { error = "Les tags ne se modifient que dans la bibliothèque musique." });
        }

        var files = LibraryFs.AudioFiles(r, Clean(request.Items), KindOf(), MaxTagFiles, out var truncated);
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Aucun fichier audio dans cette sélection." });
        }

        var snapshots = new List<(string Path, TagSnapshot Tags)>();
        foreach (var file in files)
        {
            if (LibraryFs.TryResolve(r, file, out var full, out _) && LibraryTags.Read(full) is { } t)
            {
                snapshots.Add((file, t));
            }
        }

        List<object> Values(Func<TagSnapshot, string?> pick) => snapshots.Select(s => pick(s.Tags)).Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v!).OrderByDescending(g => g.Count()).Take(30).Select(g => (object)new { Value = g.Key, Count = g.Count() }).ToList();
        string? Num(uint v) => v == 0 ? null : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ok(new
        {
            Files = snapshots.Count,
            Truncated = truncated,
            WithCover = snapshots.Count(s => s.Tags.HasCover),
            Fields = new
            {
                Title = snapshots.Count == 1 ? Values(t => t.Title) : new List<object>(),
                Artist = Values(t => t.Artist),
                AlbumArtist = Values(t => t.AlbumArtist),
                Album = Values(t => t.Album),
                Year = Values(t => Num(t.Year)),
                Track = snapshots.Count == 1 ? Values(t => Num(t.Track)) : new List<object>(),
                Disc = Values(t => Num(t.Disc)),
                Genre = Values(t => t.Genre)
            },
            Samples = snapshots.Take(8).Select(s => new { s.Path, s.Tags.Title, Track = s.Tags.Track == 0 ? (uint?)null : s.Tags.Track }).ToList()
        });
    }

    /// <summary>
    /// Modifie les tags des morceaux d'éléments (un morceau, ou tous ceux de dossiers entiers), et peut les ranger dans « Artiste/Album » d'après leurs nouveaux tags
    /// (ce qui permet de renommer un album ou de fusionner un album coupé en deux).
    /// </summary>
    /// <param name="request">Éléments, modifications, rangement.</param>
    /// <returns>Résultat par fichier.</returns>
    [HttpPost("Library/Tags/Apply")]
    public ActionResult ApplyTags([FromBody] LibraryTagsRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!CanMove)
        {
            return Forbid();
        }

        if (r!.Key != "music")
        {
            return BadRequest(new { error = "Les tags ne se modifient que dans la bibliothèque musique." });
        }

        var changes = request.Changes ?? new TagChanges();
        if (changes.IsEmpty && !request.Reorganize)
        {
            return BadRequest(new { error = "Aucune modification demandée." });
        }

        var files = LibraryFs.AudioFiles(r, Clean(request.Items), KindOf(), MaxTagFiles, out var truncated);
        if (files.Count == 0)
        {
            return BadRequest(new { error = "Aucun fichier audio dans cette sélection." });
        }

        if (truncated)
        {
            return BadRequest(new { error = $"Trop de fichiers (plus de {MaxTagFiles}) : choisissez une sélection plus petite." });
        }

        var policy = request.Policy == "rename" ? "rename" : "skip";
        var rows = new List<TagRow>();
        foreach (var file in files)
        {
            LibraryFs.TryResolve(r, file, out var full, out _);
            var before = LibraryTags.Read(full);
            var row = new TagRow { Path = file, Full = full, Before = before };
            if (before is null)
            {
                row.Status = "error";
                row.Message = "Tags illisibles.";
                rows.Add(row);
                continue;
            }

            if (IsBusy(full))
            {
                row.Status = "error";
                row.Message = "Envoi ou import en cours.";
                rows.Add(row);
                continue;
            }

            row.After = Merge(before, changes);
            if (request.Reorganize)
            {
                var artistDir = FirstOf(row.After.AlbumArtist) ?? FirstOf(row.After.Artist);
                var dir = Config.MusicLayout == "flat" ? r.Path : PathBuilder.MusicDir(r.Path, artistDir, row.After.Album);
                row.TargetRelative = LibraryFs.Relative(r, Path.Combine(dir, Path.GetFileName(full)));
                if (string.Equals(row.TargetRelative, file, StringComparison.Ordinal))
                {
                    row.TargetRelative = null;
                }
                else if (System.IO.File.Exists(Path.Combine(dir, Path.GetFileName(full))))
                {
                    row.Conflict = true;
                }
            }

            row.Status = "ok";
            rows.Add(row);
        }

        if (request.DryRun)
        {
            return Ok(new
            {
                Files = rows.Count,
                Errors = rows.Count(x => x.Status == "error"),
                Moves = rows.Count(x => x.TargetRelative is not null),
                Conflicts = rows.Count(x => x.Conflict),
                Rows = rows.Take(150).Select(x => new { x.Path, x.Status, x.Message, x.Conflict, Target = x.TargetRelative, Before = Brief(x.Before), After = Brief(x.After) }).ToList()
            });
        }

        // Écriture des tags, puis rangement éventuel.
        var capturedTags = request.Reorganize ? CaptureItems(r, files) : new Dictionary<Guid, string>();
        var touchedDirs = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var touched = new List<string>();
        foreach (var row in rows.Where(x => x.Status == "ok"))
        {
            if (!changes.IsEmpty)
            {
                var error = LibraryTags.Apply(row.Full, changes);
                if (error is not null)
                {
                    row.Status = "error";
                    row.Message = error;
                    continue;
                }
            }

            row.Status = "tagged";
            touched.Add(row.Path);
            if (row.TargetRelative is not null)
            {
                LibraryFs.TryResolve(r, row.TargetRelative, out var targetFull, out _);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
                    if (System.IO.File.Exists(targetFull))
                    {
                        if (policy == "skip")
                        {
                            row.Message = "Un fichier du même nom existe déjà dans le dossier de destination : non déplacé.";
                            continue;
                        }

                        targetFull = UniqueName(targetFull);
                    }

                    System.IO.File.Move(row.Full, targetFull);
                    removed.Add(row.Path);
                    touchedDirs.Add(Path.GetDirectoryName(row.Full)!);
                    row.TargetRelative = LibraryFs.Relative(r, targetFull);
                    row.Status = "moved";
                    row.Moved = true;
                    touched.Add(row.TargetRelative);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    row.Message = ex.Message;
                }
            }
        }

        if (request.Reorganize)
        {
            CleanupAfterReorganize(r, rows, touchedDirs, removed, touched);
        }

        var ok = rows.Count(x => x.Status is "tagged" or "moved");
        if (ok > 0)
        {
            SizeCache.Clear();
            var movedFiles = rows.Where(x => x.Moved && x.TargetRelative is not null).ToDictionary(x => x.Full, x => Full(r, x.TargetRelative!), StringComparer.Ordinal);
            Relink(capturedTags, old => movedFiles.TryGetValue(old, out var moved) ? moved : null);
            var summary = new List<string>();
            if (changes.Album is not null)
            {
                summary.Add($"album « {changes.Album} »");
            }

            if (changes.AlbumArtist is not null)
            {
                summary.Add($"artiste de l'album « {changes.AlbumArtist} »");
            }

            if (changes.Artist is not null)
            {
                summary.Add($"artiste « {changes.Artist} »");
            }

            if (changes.Year is not null)
            {
                summary.Add($"année {changes.Year}");
            }

            if (changes.Genre is not null)
            {
                summary.Add($"genre « {changes.Genre} »");
            }

            if (changes.Title is not null || changes.Track is not null || changes.Disc is not null)
            {
                summary.Add("titre/piste/disque");
            }

            var movedCount = rows.Count(x => x.Moved);
            var name = $"{CurrentUserName} a modifié les tags de {ok} fichier{(ok > 1 ? "s" : string.Empty)}" + (movedCount > 0 ? $" et en a rangé {movedCount}" : string.Empty) + $" ({r.Label})";
            var lines = rows.Where(x => x.Status is "tagged" or "moved").Select(x => $"{x.Path}" + (x.Before is null || x.After is null ? string.Empty : " : " + Diff(x.Before, x.After)) + (x.Moved ? $" → {x.TargetRelative}" : string.Empty)).ToList();
            PluginActivity.Log(name, "MediaUploaderLibrary", CurrentUserId, string.Join(", ", summary), ActivityText.LibraryDetail(lines, MaxActivityLines));
            AfterChange(r, removed, touched, tagsChanged: !changes.IsEmpty);
        }

        return Ok(new { Files = rows.Count, Done = ok, Errors = rows.Count(x => x.Status == "error"), Skipped = rows.Count(x => x.Status == "tagged" && x.TargetRelative is not null), Rows = rows.Take(150).Select(x => new { x.Path, x.Status, x.Message, Target = x.TargetRelative }).ToList() });
    }

    private sealed class TagRow
    {
        public string Path { get; set; } = string.Empty;

        public string Full { get; set; } = string.Empty;

        public TagSnapshot? Before { get; set; }

        public TagSnapshot? After { get; set; }

        public string? TargetRelative { get; set; }

        public bool Conflict { get; set; }

        public bool Moved { get; set; }

        public string Status { get; set; } = "ok";

        public string? Message { get; set; }
    }

    private string Full(LibraryRoot root, string relative)
    {
        LibraryFs.TryResolve(root, relative, out var full, out _);
        return full;
    }

    // Nouveau chemin d'un ancien chemin dont un dossier parent (ou lui-même) a été déplacé ; null si le fichier n'a pas bougé.
    private static string? MapPrefix(IEnumerable<(string From, string To)> prefixes, string old)
    {
        if (System.IO.File.Exists(old))
        {
            return null;
        }

        foreach (var (from, to) in prefixes)
        {
            if (string.Equals(old, from, StringComparison.Ordinal))
            {
                return to;
            }

            if (old.StartsWith(from.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Path.Combine(to, old[(from.TrimEnd(Path.DirectorySeparatorChar).Length + 1)..]);
            }
        }

        return null;
    }

    // Médias que Jellyfin connaît déjà dans ces éléments : identifiant et chemin avant l'action (au plus 3000).
    private Dictionary<Guid, string> CaptureItems(LibraryRoot root, IEnumerable<string> relatives)
    {
        var map = new Dictionary<Guid, string>();
        var kindOf = KindOf();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        try
        {
            foreach (var relative in relatives)
            {
                if (!LibraryFs.TryResolve(root, relative, out var full, out _))
                {
                    continue;
                }

                IEnumerable<string> files = System.IO.File.Exists(full) ? new[] { full } : Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", options) : Array.Empty<string>();
                foreach (var file in files)
                {
                    if (map.Count >= 3000)
                    {
                        return map;
                    }

                    if (kindOf(Path.GetExtension(file).ToLowerInvariant()) is "audio" or "video" && _libraryManager.FindByPath(file, false) is { } item)
                    {
                        map[item.Id] = file;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaUploader: lecture des médias connus de Jellyfin impossible");
        }

        return map;
    }

    private void Relink(Dictionary<Guid, string> captured, Func<string, string?> newPathOf)
    {
        if (captured.Count == 0 || Config.LibraryRefresh == "none")
        {
            return;
        }

        try
        {
            var n = PlaylistRelinker.Queue(_libraryManager, _playlistManager, _userManager, captured, newPathOf, _logger);
            if (n > 0)
            {
                _logger.LogInformation("MediaUploader: {Count} entrée(s) de listes de lecture seront remises à leur place après l'actualisation de Jellyfin", n);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaUploader: réparation des listes de lecture impossible");
        }
    }

    private static string? FirstOf(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string UniqueName(string path)
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

    // Tags tels qu'ils seront après application des modifications (pour l'aperçu et pour calculer le dossier « Artiste/Album »).
    private static TagSnapshot Merge(TagSnapshot t, TagChanges c)
    {
        static string? Text(string? changed, string? old) => changed is null ? old : changed.Length == 0 ? null : changed;
        return new TagSnapshot(
            Text(c.Title, t.Title),
            Text(c.Artist, t.Artist),
            Text(c.AlbumArtist, t.AlbumArtist),
            Text(c.Album, t.Album),
            c.Year ?? t.Year,
            c.Track ?? t.Track,
            c.Disc ?? t.Disc,
            Text(c.Genre, t.Genre),
            t.HasCover);
    }

    private static object? Brief(TagSnapshot? t) => t is null ? null : new { t.Title, t.Artist, t.AlbumArtist, t.Album, Year = t.Year == 0 ? (uint?)null : t.Year, Track = t.Track == 0 ? (uint?)null : t.Track, Disc = t.Disc == 0 ? (uint?)null : t.Disc, t.Genre };

    private static string Diff(TagSnapshot a, TagSnapshot b)
    {
        var parts = new List<string>();
        void Add(string label, string? x, string? y)
        {
            if (!string.Equals(x ?? string.Empty, y ?? string.Empty, StringComparison.Ordinal))
            {
                parts.Add($"{label} « {x} » → « {y} »");
            }
        }

        string? Num(uint v) => v == 0 ? null : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Add("titre", a.Title, b.Title);
        Add("artiste", a.Artist, b.Artist);
        Add("artiste album", a.AlbumArtist, b.AlbumArtist);
        Add("album", a.Album, b.Album);
        Add("genre", a.Genre, b.Genre);
        Add("année", Num(a.Year), Num(b.Year));
        Add("piste", Num(a.Track), Num(b.Track));
        Add("disque", Num(a.Disc), Num(b.Disc));
        return parts.Count == 0 ? "inchangé" : string.Join(" ; ", parts);
    }

    // Après avoir rangé des morceaux : les fichiers annexes (pochette, paroles…) du dossier d'origine suivent, et les dossiers vidés disparaissent.
    private void CleanupAfterReorganize(LibraryRoot root, List<TagRow> rows, HashSet<string> sourceDirs, List<string> removed, List<string> touched)
    {
        var kindOf = KindOf();
        foreach (var dir in sourceDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var left = new DirectoryInfo(dir).EnumerateFiles().ToList();
                var stillHasAudio = left.Any(f => kindOf(f.Extension.ToLowerInvariant()) == "audio");
                if (!stillHasAudio)
                {
                    // Tous les morceaux sont partis au même endroit : les annexes les y suivent (sans rien écraser).
                    var targets = rows.Where(x => x.Moved && string.Equals(Path.GetDirectoryName(x.Full), dir, StringComparison.Ordinal)).Select(x => Path.GetDirectoryName(Path.Combine(root.Path, x.TargetRelative!.Replace('/', Path.DirectorySeparatorChar)))!).Distinct(StringComparer.Ordinal).ToList();
                    if (targets.Count == 1)
                    {
                        foreach (var sidecar in left)
                        {
                            var destination = Path.Combine(targets[0], sidecar.Name);
                            if (!System.IO.File.Exists(destination))
                            {
                                sidecar.MoveTo(destination);
                                removed.Add(LibraryFs.Relative(root, sidecar.FullName));
                                touched.Add(LibraryFs.Relative(root, destination));
                            }
                        }
                    }
                }

                // Remonte en supprimant les dossiers vides, sans jamais toucher à la racine.
                var current = dir;
                while (!string.Equals(current, root.Path, StringComparison.Ordinal) && Directory.Exists(current) && !new DirectoryInfo(current).EnumerateFileSystemInfos().Any())
                {
                    Directory.Delete(current);
                    removed.Add(LibraryFs.Relative(root, current));
                    current = Path.GetDirectoryName(current)!;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "MediaUploader: nettoyage du dossier {Dir} impossible", dir);
            }
        }
    }

    /// <summary>
    /// Cherche des pochettes pour un album (Cover Art Archive et iTunes).
    /// </summary>
    /// <param name="artist">Artiste.</param>
    /// <param name="album">Album.</param>
    /// <returns>Propositions.</returns>
    [HttpGet("Library/Cover/Search")]
    public async Task<ActionResult> SearchCovers([FromQuery] string? artist, [FromQuery] string? album)
    {
        if (!CanView)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(album) || album.Length > 200 || (artist?.Length ?? 0) > 200)
        {
            return BadRequest(new { error = "Indiquez le titre de l'album." });
        }

        return Ok(new { Candidates = await CoverSearch.SearchAsync(artist?.Trim(), album.Trim(), HttpContext.RequestAborted).ConfigureAwait(false) });
    }

    /// <summary>
    /// Ajoute une pochette à un album : enregistrée dans le dossier (cover.jpg) et/ou intégrée à chaque morceau.
    /// </summary>
    /// <param name="request">Dossier et image (proposition choisie, ou image envoyée).</param>
    /// <returns>Bilan.</returns>
    [HttpPost("Library/Cover/Apply")]
    public async Task<ActionResult> ApplyCover([FromBody] LibraryCoverRequest request)
    {
        if (!Gate(request.Root, out var r, out var denied))
        {
            return denied!;
        }

        if (!CanMove)
        {
            return Forbid();
        }

        if (r!.Key != "music")
        {
            return BadRequest(new { error = "Les pochettes se gèrent dans la bibliothèque musique." });
        }

        if (!LibraryFs.TryResolve(r, request.Path, out var folder, out var error) || !Directory.Exists(folder) || LibraryFs.HasLink(r, folder) || string.Equals(folder, r.Path, StringComparison.Ordinal))
        {
            return BadRequest(new { error = error ?? "Choisissez le dossier d'un album." });
        }

        byte[]? image = null;
        string label;
        if (!string.IsNullOrEmpty(request.ImageBase64))
        {
            var text = request.ImageBase64;
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            if (text.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
            {
                text = text[(comma + 1)..];
            }

            if (text.Length > 12_000_000)
            {
                return BadRequest(new { error = "Image trop grosse (8 Mo au plus)." });
            }

            try
            {
                image = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Image illisible." });
            }

            label = "image envoyée";
        }
        else if (request.Source == "musicbrainz" && Guid.TryParse(request.Id, out var mbid))
        {
            image = await Services.Import.TrackTagger.FetchCoverAsync($"https://coverartarchive.org/release-group/{mbid}/front-1200", HttpContext.RequestAborted, _logger).ConfigureAwait(false)
                ?? await Services.Import.TrackTagger.FetchCoverAsync($"https://coverartarchive.org/release-group/{mbid}/front-500", HttpContext.RequestAborted, _logger).ConfigureAwait(false);
            label = "Cover Art Archive (MusicBrainz " + mbid + ")";
        }
        else if (request.Source == "itunes" && Services.Import.CoverHosts.Allowed(request.Url, out var iuri))
        {
            image = await Services.Import.TrackTagger.FetchCoverAsync(iuri!.ToString(), HttpContext.RequestAborted, _logger).ConfigureAwait(false);
            label = "iTunes";
        }
        else
        {
            return BadRequest(new { error = "Choisissez une pochette proposée ou envoyez une image." });
        }

        var mime = image is null || image.Length > 8 * 1024 * 1024 ? null : Services.Import.CoverHosts.ImageMime(image);
        if (image is null || mime is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Image introuvable ou format non pris en charge (JPEG ou PNG)." });
        }

        var saved = false;
        string? savedName = null;
        if (request.SaveFile != false)
        {
            var existing = Directory.EnumerateFiles(folder).Where(f => new[] { "cover", "folder", "poster", "front", "albumart" }.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()) && new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            if (existing.Count == 0 || request.Overwrite)
            {
                foreach (var old in existing)
                {
                    System.IO.File.Delete(old);
                }

                savedName = "cover" + (mime == "image/png" ? ".png" : ".jpg");
                await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, savedName), image, HttpContext.RequestAborted).ConfigureAwait(false);
                saved = true;
            }
        }

        int embedded = 0, skipped = 0, errors = 0;
        var touched = new List<string>();
        if (request.Embed != false)
        {
            foreach (var file in LibraryFs.AudioFiles(r, new[] { request.Path! }, KindOf(), MaxTagFiles, out _))
            {
                LibraryFs.TryResolve(r, file, out var full, out _);
                if (IsBusy(full) || (!request.Overwrite && LibraryTags.HasCover(full)))
                {
                    skipped++;
                    continue;
                }

                if (LibraryTags.SetCover(full, image, mime) is null)
                {
                    embedded++;
                    touched.Add(file);
                }
                else
                {
                    errors++;
                }
            }
        }

        if (saved || embedded > 0)
        {
            SizeCache.Clear();
            PluginActivity.Log(
                $"{CurrentUserName} a ajouté une pochette à « {request.Path} » ({r.Label})",
                "MediaUploaderLibrary",
                CurrentUserId,
                $"source : {label}" + (saved ? $" · {savedName} enregistré" : string.Empty) + (embedded > 0 ? $" · intégrée à {embedded} morceau(x)" : string.Empty),
                ActivityText.LibraryDetail(touched.Select(t => t + " : pochette intégrée").Prepend(saved ? $"{request.Path}/{savedName}" : "(pas de fichier cover enregistré)").ToList(), MaxActivityLines));
            AfterChange(r, Array.Empty<string>(), touched.Append(request.Path!), tagsChanged: embedded > 0, imagesChanged: true);
        }

        return Ok(new { Saved = saved, FileName = savedName, Embedded = embedded, Skipped = skipped, Errors = errors, Source = label });
    }

    private static List<string> Clean(IEnumerable<string>? items) => (items ?? Enumerable.Empty<string>()).Where(i => i is not null).Select(i => i.Trim()).Distinct(StringComparer.Ordinal).ToList();

    private static string MimeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" or ".alac" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".ts" => "video/mp2t",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".srt" or ".vtt" or ".lrc" or ".nfo" or ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private List<LibraryRoot> Roots() => LibraryFs.Roots(Config.MusicPath, Config.MoviesPath, Config.ShowsPath);

    private Func<string, string> KindOf()
    {
        var o = PlanFactory.Options(Config);
        return ext => o.Audio.Contains(ext) ? "audio" : o.Video.Contains(ext) ? "video" : o.Extra.Contains(ext) ? "extra" : "other";
    }

    // Vérifie le droit de voir et retrouve la bibliothèque demandée.
    private bool Gate(string? key, out LibraryRoot? root, out ActionResult? denied)
    {
        root = null;
        denied = null;
        if (!CanView)
        {
            denied = Forbid();
            return false;
        }

        root = Roots().FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
        if (root is null)
        {
            denied = BadRequest(new { error = "Bibliothèque inconnue ou dossier non configuré." });
            return false;
        }

        return true;
    }

    private bool UserCanDeleteContent()
    {
        var user = _userManager.GetUserById(CurrentUserId);
        return user is not null && user.HasPermission(PermissionKind.EnableContentDeletion);
    }

    // Un envoi ou un import en cours écrit dans cet élément : on n'y touche pas.
    private bool IsBusy(string full)
    {
        bool Inside(string? temp) => temp is not null && (string.Equals(temp, full, StringComparison.Ordinal) || temp.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (UploadStore.Sessions.Values.Any(s => Inside(s.TempPath)))
        {
            return true;
        }

        foreach (var batch in UploadStore.Batches.Values)
        {
            lock (batch.Sync)
            {
                if (batch.Items.Any(i => Inside(i.TempPath)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Tient la médiathèque de Jellyfin à jour après une action : ce qui n'existe plus en est retiré tout de suite (sinon l'élément reste affiché et sa lecture échoue),
    // puis la bibliothèque concernée est actualisée (comme le bouton « Actualiser » de Jellyfin) pour voir les fichiers ajoutés, déplacés ou aux tags modifiés.
    private void AfterChange(LibraryRoot root, IEnumerable<string> removedRelatives, IEnumerable<string> touchedRelatives, bool tagsChanged = false, bool imagesChanged = false)
    {
        var mode = Config.LibraryRefresh;
        if (mode == "none")
        {
            return;
        }

        try
        {
            foreach (var relative in removedRelatives)
            {
                if (LibraryFs.TryResolve(root, relative, out var full, out _) && !System.IO.File.Exists(full) && !Directory.Exists(full) && !string.Equals(full, root.Path, StringComparison.Ordinal))
                {
                    RemoveFromLibrary(root, full);
                }
            }

            if (mode == "full")
            {
                _libraryManager.QueueLibraryScan();
                return;
            }

            // D'abord une validation rapide (les fichiers déplacés, restaurés ou retirés apparaissent en quelques secondes), puis, si des tags ont changé, une relecture des métadonnées.
            RefreshLibrary(root, false, false);
            if (tagsChanged || imagesChanged)
            {
                RefreshLibrary(root, true, imagesChanged);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaUploader: mise à jour de la médiathèque Jellyfin impossible après une action sur la bibliothèque");
        }
    }

    // Actualise le dossier racine d'une bibliothèque dans Jellyfin, sans images : seuls les fichiers nouveaux ou modifiés sont examinés.
    // (Prévenir seulement le sous-dossier touché ne suffit pas : Jellyfin ne découvre alors pas les nouveaux fichiers.)
    private void RefreshLibrary(LibraryRoot root, bool readMetadata, bool readImages)
    {
        var item = _libraryManager.FindByPath(root.Path, true);
        if (item is null)
        {
            _libraryManager.QueueLibraryScan();
            return;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = readMetadata ? MetadataRefreshMode.Default : MetadataRefreshMode.ValidationOnly,
            ImageRefreshMode = readImages ? MetadataRefreshMode.Default : MetadataRefreshMode.None,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };
        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
    }

    // Retire de la base de Jellyfin un élément dont le fichier ou le dossier n'existe plus (les fichiers ne sont jamais touchés ici).
    private void RemoveFromLibrary(LibraryRoot root, string full)
    {
        foreach (var isFolder in new bool?[] { true, false })
        {
            var item = _libraryManager.FindByPath(full, isFolder);
            if (item is null || string.Equals(item.Path, root.Path, StringComparison.Ordinal) || !string.Equals(item.Path, full, StringComparison.Ordinal))
            {
                continue;
            }

            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            return;
        }
    }
}
