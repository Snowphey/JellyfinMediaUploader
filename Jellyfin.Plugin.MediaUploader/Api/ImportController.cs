using System.Net.Mime;
using Jellyfin.Plugin.MediaUploader.Services;
using Jellyfin.Plugin.MediaUploader.Services.Import;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>Lien à importer.</summary>
public class ImportLinkRequest
{
    /// <summary>Gets or sets le lien Spotify, YouTube ou YouTube Music.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets le rangement : "source" ou "playlist".</summary>
    public string? Layout { get; set; }
}

/// <summary>Un album choisi dans la navigation.</summary>
public class ImportAlbumRef
{
    /// <summary>Gets or sets l'identifiant MusicBrainz du groupe de sorties.</summary>
    public string? Id { get; set; }
}

/// <summary>Albums à importer en une fois.</summary>
public class ImportAlbumsRequest
{
    /// <summary>Gets or sets le nom de l'artiste (titre de l'import).</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets les albums.</summary>
    public List<ImportAlbumRef> Albums { get; set; } = new();
}

/// <summary>Démarrage des téléchargements d'un import prêt.</summary>
public class ImportStartRequest
{
    /// <summary>Gets or sets les morceaux retenus (tous si omis).</summary>
    public List<string>? TrackIds { get; set; }

    /// <summary>Gets or sets le rangement : "source" ou "playlist".</summary>
    public string? Layout { get; set; }

    /// <summary>Gets or sets a value indicating whether une liste de lecture Jellyfin est créée (réglage par défaut si omis).</summary>
    public bool? CreatePlaylist { get; set; }

    /// <summary>Gets or sets a value indicating whether cette liste est publique (réglage par défaut si omis).</summary>
    public bool? PlaylistPublic { get; set; }
}

/// <summary>Un morceau d'un import.</summary>
/// <param name="Id">Identifiant.</param>
/// <param name="Title">Titre.</param>
/// <param name="Artist">Artiste.</param>
/// <param name="Album">Album.</param>
/// <param name="TrackNo">Numéro de piste.</param>
/// <param name="Duration">Durée en secondes.</param>
/// <param name="State">État.</param>
/// <param name="Message">Détail (cause d'un échec).</param>
/// <param name="Selected">Retenu pour le téléchargement.</param>
/// <param name="Phase">Étape en cours (attente, recherche, téléchargement, conversion, tags, rangement).</param>
/// <param name="Percent">Avancement du téléchargement en cours (%).</param>
public record ImportTrackView(string Id, string Title, string Artist, string? Album, int? TrackNo, int? Duration, string State, string? Message, bool Selected, string? Phase, int? Percent);

/// <summary>État d'un import.</summary>
/// <param name="Id">Identifiant.</param>
/// <param name="State">"resolving", "ready", "downloading", "done", "failed" ou "cancelled".</param>
/// <param name="Message">Erreur ou bilan.</param>
/// <param name="Source">"youtube", "spotify" ou "browse".</param>
/// <param name="Kind">"playlist", "album" ou "track".</param>
/// <param name="Title">Titre de la liste.</param>
/// <param name="CoverUrl">Pochette.</param>
/// <param name="Layout">Rangement.</param>
/// <param name="BatchId">Lot alimenté (dès que les téléchargements ont démarré).</param>
/// <param name="Total">Morceaux retenus.</param>
/// <param name="Done">Morceaux reçus.</param>
/// <param name="Failed">Morceaux en échec.</param>
/// <param name="WaitReason">Pourquoi aucun téléchargement ne démarre pour l'instant : "paused", "hour", "day" ou "pace" (délai entre deux morceaux), sinon null.</param>
/// <param name="WaitSeconds">Attente restante en secondes.</param>
/// <param name="Tracks">Morceaux.</param>
public record ImportJobView(string Id, string State, string? Message, string Source, string Kind, string Title, string? CoverUrl, string Layout, string? BatchId, int Total, int Done, int Failed, string? WaitReason, int WaitSeconds, IReadOnlyList<ImportTrackView> Tracks);

/// <summary>
/// Import par lien (Spotify, YouTube, YouTube Music) et navigation par artistes et albums (MusicBrainz).
/// Les morceaux téléchargés arrivent dans un lot du flux habituel : aperçu du rangement, corrections, confirmation.
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class ImportController : MediaUploaderControllerBase
{
    private const int MaxAlbumsPerRequest = 20;

    private readonly IMediaEncoder _encoder;
    private readonly ILogger<ImportController> _logger;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="ImportController"/>.
    /// </summary>
    /// <param name="encoder">Encodeur multimédia de Jellyfin (fournit le chemin de ffmpeg).</param>
    /// <param name="logger">Logger.</param>
    public ImportController(IMediaEncoder encoder, ILogger<ImportController> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>
    /// Outils disponibles et réglages d'import.
    /// </summary>
    /// <returns>État.</returns>
    [HttpGet("Import/Status")]
    public async Task<ActionResult> GetStatus([FromQuery] bool versions = false)
    {
        // Lire une version lance l'outil (lent) et interroge GitHub : réservé aux administrateurs, comme la page qui l'affiche.
        versions = versions && IsAdmin;
        var ctx = BuildContext();
        var ct = HttpContext.RequestAborted;

        // Les versions installées par le plugin sont lues dans tools.json (instantané) ; lancer un outil externe prend plusieurs secondes.
        // La dernière version publiée n'est cherchée que pour la page des paramètres.
        var toolsDir = ToolsDir();
        async Task<object> Describe(string tool, string? path)
        {
            string? version = null;
            string? latest = null;
            if (path is not null)
            {
                version = ToolManifest.Known(toolsDir, tool, path) ?? (versions ? await ToolInstaller.VersionAsync(path, ct).ConfigureAwait(false) : ToolInstaller.CachedVersion(path));
            }

            if (versions)
            {
                latest = await ToolInstaller.LatestTagAsync(tool, ct).ConfigureAwait(false);
            }

            return new
            {
                Found = path is not null,
                Version = version,
                Latest = latest,
                UpdateAvailable = path is not null && ToolInstaller.IsOutdated(version, latest),
                Path = IsAdmin ? path : null
            };
        }

        var yt = Describe("yt-dlp", ctx.YtDlp);
        var dn = Describe("deno", ctx.Deno);
        var paused = ImportThrottle.PausedUntil();
        return Ok(new
        {
            Enabled = Config.ImportEnabled,
            CanUpload,
            IsAdmin,
            MusicConfigured = !string.IsNullOrWhiteSpace(Config.MusicPath),
            Format = ctx.Format,
            Platform = ToolInstaller.Current().ToString(),
            YtDlp = await yt.ConfigureAwait(false),
            Deno = await dn.ConfigureAwait(false),
            Ffmpeg = ctx.Ffmpeg is not null,
            CreatePlaylist = Config.ImportCreatePlaylist,
            PlaylistPublic = Config.ImportPlaylistPublic,
            PausedUntil = paused,
            PausedMinutes = paused is null ? 0 : (int)Math.Ceiling((paused.Value - DateTime.UtcNow).TotalMinutes)
        });
    }

    /// <summary>
    /// Installe ou met à jour yt-dlp ou deno dans le dossier du plugin (administrateurs).
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <returns>Chemin installé.</returns>
    [HttpPost("Import/Tools/{tool}")]
    public async Task<ActionResult> InstallTool(string tool)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        if (tool is not ("yt-dlp" or "deno" or "all"))
        {
            return BadRequest(new { error = "Outil inconnu." });
        }

        try
        {
            var ct = HttpContext.RequestAborted;
            var dir = ToolsDir();
            var done = new List<string>();
            foreach (var name in tool == "all" ? new[] { "yt-dlp", "deno" } : new[] { tool })
            {
                // « all » ne retélécharge pas un outil déjà à jour.
                if (tool == "all")
                {
                    var existing = ToolLocator.FindInstalled(name, dir);
                    var known = existing is null ? null : ToolManifest.Known(dir, name, existing);
                    if (existing is not null && known is not null && !ToolInstaller.IsOutdated(known, await ToolInstaller.LatestTagAsync(name, ct).ConfigureAwait(false)))
                    {
                        continue;
                    }
                }

                var path = await ToolInstaller.InstallAsync(name, dir, ct).ConfigureAwait(false);
                _logger.LogInformation("MediaUploader: {Tool} installé dans {Path}", name, path);
                done.Add(name);
            }

            return Ok(new { installed = done });
        }
        catch (ImportException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lit un lien Spotify ou YouTube : la liste des morceaux s'affiche pour que l'utilisateur choisisse avant de télécharger.
    /// </summary>
    /// <param name="request">Lien.</param>
    /// <returns>Import en cours de résolution.</returns>
    [HttpPost("Import/Jobs")]
    public ActionResult<ImportJobView> CreateFromLink([FromBody] ImportLinkRequest request)
    {
        var denied = Guard();
        if (denied is not null)
        {
            return denied;
        }

        var link = ImportUrl.Parse(request.Url, out var error);
        if (link is null)
        {
            return BadRequest(new { error });
        }

        if (link.Kind == "artist")
        {
            return BadRequest(new { error = "Pour un artiste, utilisez l'onglet « Parcourir » : vous choisirez ses albums." });
        }

        var ctx = BuildContext();
        if (ctx.YtDlp is null)
        {
            return BadRequest(new { error = "yt-dlp n'est pas installé : un administrateur peut l'installer dans les paramètres du plugin." });
        }

        var job = NewJob(link.Source == ImportSource.Spotify ? "spotify" : "youtube");
        job.Layout = ImportNaming.NormalizeLayout(request.Layout);
        ImportManager.StartResolve(job, ctx, ct => link.Source == ImportSource.Spotify ? SpotifyClient.ListAsync(link, ct) : YtDlpClient.ListAsync(ctx, link, ct), _logger);
        return View(job);
    }

    /// <summary>
    /// Importe des albums choisis dans la navigation : les pistes viennent de MusicBrainz, le téléchargement démarre tout de suite.
    /// </summary>
    /// <param name="request">Albums.</param>
    /// <returns>Import en cours de résolution.</returns>
    [HttpPost("Import/Albums")]
    public ActionResult<ImportJobView> CreateFromAlbums([FromBody] ImportAlbumsRequest request)
    {
        var denied = Guard();
        if (denied is not null)
        {
            return denied;
        }

        var ids = request.Albums.Select(a => a.Id).Where(i => Guid.TryParse(i, out _)).Select(i => i!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0 || ids.Count > MaxAlbumsPerRequest)
        {
            return BadRequest(new { error = $"Choisissez de 1 à {MaxAlbumsPerRequest} albums." });
        }

        var ctx = BuildContext();
        if (ctx.YtDlp is null)
        {
            return BadRequest(new { error = "yt-dlp n'est pas installé : un administrateur peut l'installer dans les paramètres du plugin." });
        }

        var artist = (request.Artist ?? string.Empty).Trim();
        var job = NewJob("browse");
        job.AutoStart = true;
        job.Layout = "source";
        ImportManager.StartResolve(job, ctx, async ct =>
        {
            var merged = new Listing { Kind = "album", Title = artist.Length > 0 ? artist : "Albums" };
            foreach (var id in ids)
            {
                var album = await MusicBrainzClient.AlbumListingAsync(id, ct).ConfigureAwait(false);
                foreach (var t in album.Tracks)
                {
                    merged.Tracks.Add(t.WithId("t" + (merged.Tracks.Count + 1)));
                }

                merged.CoverUrl ??= album.CoverUrl;
                if (ids.Count == 1 && artist.Length == 0)
                {
                    merged.Title = album.Title;
                }
            }

            return merged;
        }, _logger);
        return View(job);
    }

    /// <summary>
    /// État d'un import.
    /// </summary>
    /// <param name="id">Identifiant.</param>
    /// <returns>Import.</returns>
    [HttpGet("Import/Jobs/{id}")]
    public ActionResult<ImportJobView> GetJob(string id)
    {
        var job = Find(id, out var error);
        if (job is null)
        {
            return error!;
        }

        return View(job);
    }

    /// <summary>
    /// Démarre les téléchargements des morceaux retenus.
    /// </summary>
    /// <param name="id">Identifiant.</param>
    /// <param name="request">Morceaux retenus et rangement.</param>
    /// <returns>Import.</returns>
    [HttpPost("Import/Jobs/{id}/Start")]
    public async Task<ActionResult<ImportJobView>> Start(string id, [FromBody] ImportStartRequest request)
    {
        var job = Find(id, out var error);
        if (job is null)
        {
            return error!;
        }

        job.CreatePlaylist = request.CreatePlaylist ?? Config.ImportCreatePlaylist;
        job.PlaylistPublic = request.PlaylistPublic ?? Config.ImportPlaylistPublic;
        try
        {
            await ImportManager.BeginDownloadAsync(job, request.TrackIds, request.Layout ?? job.Layout, BuildContext(), _logger).ConfigureAwait(false);
        }
        catch (ImportException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        return View(job);
    }

    /// <summary>
    /// Arrête un import. Les morceaux déjà reçus restent dans le lot.
    /// </summary>
    /// <param name="id">Identifiant.</param>
    /// <returns>204.</returns>
    [HttpDelete("Import/Jobs/{id}")]
    public ActionResult Cancel(string id)
    {
        var job = Find(id, out var error);
        if (job is null)
        {
            return error!;
        }

        ImportManager.Cancel(job);
        return NoContent();
    }

    /// <summary>
    /// Cherche des artistes (MusicBrainz).
    /// </summary>
    /// <param name="q">Nom saisi.</param>
    /// <returns>Artistes.</returns>
    [HttpGet("Browse/Artists")]
    public async Task<ActionResult> SearchArtists([FromQuery] string? q)
    {
        var denied = Guard(requireTools: false);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(q) || q.Length > 100)
        {
            return BadRequest(new { error = "Saisissez un nom d'artiste." });
        }

        try
        {
            return Ok(new { Artists = await MusicBrainzClient.SearchArtistsAsync(q, HttpContext.RequestAborted).ConfigureAwait(false) });
        }
        catch (ImportException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Albums, EP et singles d'un artiste (MusicBrainz).
    /// </summary>
    /// <param name="id">MBID de l'artiste.</param>
    /// <returns>Sorties.</returns>
    [HttpGet("Browse/Artists/{id}/Albums")]
    public async Task<ActionResult> ArtistAlbums(string id)
    {
        var denied = Guard(requireTools: false);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            return Ok(new { Albums = await MusicBrainzClient.ArtistAlbumsAsync(id, HttpContext.RequestAborted).ConfigureAwait(false) });
        }
        catch (ImportException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Nombre de pistes d'un album (MusicBrainz), pour l'afficher avant d'importer la sélection.
    /// </summary>
    /// <param name="id">MBID du groupe de sorties.</param>
    /// <returns>Nombre de pistes.</returns>
    [HttpGet("Browse/Albums/{id}/Info")]
    public async Task<ActionResult> AlbumInfo(string id)
    {
        var denied = Guard(requireTools: false);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            return Ok(new { Tracks = await MusicBrainzClient.AlbumTrackCountAsync(id, HttpContext.RequestAborted).ConfigureAwait(false) });
        }
        catch (ImportException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Quotas de téléchargement (heure, jour), suspension éventuelle, et simulation du lancement de <paramref name="tracks"/> morceaux : sert à prévenir avant un dépassement.
    /// </summary>
    /// <param name="tracks">Nombre de morceaux que l'utilisateur s'apprête à télécharger.</param>
    /// <returns>Quotas.</returns>
    [HttpGet("Import/Quota")]
    public ActionResult GetQuota([FromQuery] int tracks = 0)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        var q = ImportThrottle.Snapshot(BuildContext(), Math.Clamp(tracks, 0, 5000));
        return Ok(new
        {
            q.MaxPerHour,
            q.UsedHour,
            RemainingHour = q.MaxPerHour > 0 ? Math.Max(0, q.MaxPerHour - q.UsedHour) : -1,
            q.MaxPerDay,
            q.UsedDay,
            RemainingDay = q.MaxPerDay > 0 ? Math.Max(0, q.MaxPerDay - q.UsedDay) : -1,
            q.MinDelay,
            q.MaxDelay,
            PausedMinutes = q.PausedFor is null ? 0 : (int)Math.Ceiling(q.PausedFor.Value.TotalMinutes),
            NextSlotMinutes = q.NextSlotIn is null ? 0 : (int)Math.Ceiling(q.NextSlotIn.Value.TotalMinutes),
            q.Tracks,
            LastStartMinutes = (int)Math.Ceiling(q.LastStartIn.TotalMinutes),
            q.Delayed
        });
    }

    private ActionResult? Guard(bool requireTools = true)
    {
        if (!CanUpload)
        {
            return Forbid();
        }

        if (!Config.ImportEnabled)
        {
            return BadRequest(new { error = "L'import par lien est désactivé par l'administrateur." });
        }

        if (requireTools)
        {
            if (string.IsNullOrWhiteSpace(Config.MusicPath))
            {
                return BadRequest(new { error = "Le dossier musique n'est pas configuré." });
            }

            ImportManager.Sweep();
            if (ImportManager.ActiveCount(CurrentOwner) >= ImportManager.MaxActiveJobsPerOwner)
            {
                return BadRequest(new { error = "Trop d'imports en cours : attendez la fin d'un import ou annulez-le." });
            }
        }

        return null;
    }

    private ImportJob NewJob(string source) => new() { Id = Guid.NewGuid().ToString("N"), Owner = CurrentOwner, Source = source };

    private ImportJob? Find(string id, out ActionResult? error)
    {
        error = null;
        if (!CanUpload)
        {
            error = Forbid();
            return null;
        }

        if (!ImportManager.Jobs.TryGetValue(id, out var job))
        {
            error = NotFound(new { error = "Import inconnu ou expiré." });
            return null;
        }

        if (job.Owner != CurrentOwner && !IsAdmin)
        {
            error = Forbid();
            return null;
        }

        return job;
    }

    private string ToolsDir() => Path.Combine(Plugin.Instance!.DataFolderPath, "tools");

    private ToolContext BuildContext()
    {
        var c = Config;
        var dir = ToolsDir();
        ImportThrottle.Use(dir);
        var ffmpeg = _encoder.EncoderPath;
        return new ToolContext
        {
            ToolsDir = dir,
            YtDlp = ToolLocator.Find("yt-dlp", c.YtDlpPath, dir),
            Deno = ToolLocator.Find("deno", null, dir),
            Ffmpeg = string.IsNullOrWhiteSpace(ffmpeg) ? null : ffmpeg,
            CookiesPath = string.IsNullOrWhiteSpace(c.ImportCookiesPath) ? null : c.ImportCookiesPath.Trim(),
            Format = ToolContext.NormalizeFormat(c.ImportAudioFormat),
            Concurrency = Math.Clamp(c.ImportConcurrency <= 0 ? 1 : c.ImportConcurrency, 1, 4),
            MusicRoot = c.MusicPath?.Trim() ?? string.Empty,
            MinDelaySeconds = Math.Clamp(c.ImportMinDelaySeconds, 2, 600),
            MaxDelaySeconds = Math.Clamp(Math.Max(c.ImportMaxDelaySeconds, c.ImportMinDelaySeconds), 2, 1800),
            MaxPerHour = Math.Clamp(c.ImportMaxPerHour, 0, 1000),
            MaxPerDay = Math.Clamp(c.ImportMaxPerDay, 0, 10000)
        };
    }

    private ImportJobView View(ImportJob job)
    {
        // Ce qui retient les téléchargements en ce moment : suspension, plafond atteint, ou simple délai entre deux morceaux.
        string? waitReason = null;
        var waitSeconds = 0;
        if (job.State == "downloading")
        {
            var q = ImportThrottle.Snapshot(BuildContext(), 0);
            (waitReason, var wait) = q.PausedFor is { } p ? ("paused", p)
                : q.NextSlotIn is { } n ? (q.MaxPerHour > 0 && q.UsedHour >= q.MaxPerHour ? "hour" : "day", n)
                : q.PaceIn is { } pace ? ("pace", pace)
                : ((string?)null, TimeSpan.Zero);
            waitSeconds = (int)Math.Ceiling(wait.TotalSeconds);
        }

        lock (job.Sync)
        {
            var shown = job.Tracks.Where(t => job.State is "resolving" or "ready" || t.Selected).ToList();
            var selected = job.Tracks.Where(t => t.Selected).ToList();
            return new ImportJobView(
                job.Id,
                job.State,
                job.Message,
                job.Source,
                job.Kind,
                job.Title,
                CoverHosts.Allowed(job.CoverUrl, out _) ? job.CoverUrl : null,
                job.Layout,
                job.BatchId,
                selected.Count,
                selected.Count(t => t.State == "done"),
                selected.Count(t => t.State == "failed"),
                waitReason,
                waitSeconds,
                shown.Select(t => new ImportTrackView(t.Spec.Id, t.Spec.Title, t.Spec.Artist, t.Spec.Album, t.Spec.TrackNo, t.Spec.DurationSec, t.State, t.Message, t.Selected, t.Phase, t.Percent)).ToList());
        }
    }
}
