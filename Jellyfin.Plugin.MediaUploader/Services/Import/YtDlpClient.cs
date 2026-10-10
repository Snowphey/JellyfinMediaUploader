using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Liste et télécharge des playlists, albums et vidéos YouTube / YouTube Music avec yt-dlp.
/// </summary>
public static class YtDlpClient
{
    /// <summary>Nombre maximal de morceaux lus dans une playlist.</summary>
    public const int MaxEntries = 600;

    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex AlbumPrefix = new(@"^Album\s*[-–—]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DownloadPercent = new(@"^\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex SongCount = new(@"\s*\(\d+\s+songs?\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Arguments pour lire le contenu d'un lien sans rien télécharger.
    /// </summary>
    /// <param name="c">Outils.</param>
    /// <param name="url">Adresse canonique.</param>
    /// <returns>Arguments.</returns>
    public static List<string> ListArgs(ToolContext c, string url)
    {
        var args = new List<string> { "--flat-playlist", "-J", "--no-warnings", "--ignore-errors", "--playlist-end", MaxEntries.ToString(System.Globalization.CultureInfo.InvariantCulture), "--socket-timeout", "30" };
        AddCommon(c, args);
        args.Add("--");
        args.Add(url);
        return args;
    }

    /// <summary>
    /// Arguments pour télécharger l'audio d'une vidéo dans un dossier.
    /// </summary>
    /// <param name="c">Outils.</param>
    /// <param name="videoUrl">Adresse de la vidéo.</param>
    /// <param name="workDir">Dossier de travail.</param>
    /// <returns>Arguments.</returns>
    public static List<string> DownloadArgs(ToolContext c, string videoUrl, string workDir)
    {
        var selector = c.Format switch
        {
            "opus" => "bestaudio[acodec=opus]/bestaudio/best",
            "mp3" => "bestaudio/best",
            _ => "bestaudio[ext=m4a]/bestaudio/best"
        };
        var args = new List<string>
        {
            "-f", selector, "-x", "--audio-format", c.Format, "--audio-quality", "0",
            "--no-playlist", "--no-warnings", "--progress", "--newline", "--no-mtime",
            "--retries", "3", "--fragment-retries", "3", "--socket-timeout", "30",
            "-P", workDir, "-o", "%(id)s.%(ext)s"
        };
        AddCommon(c, args);
        args.Add("--");
        args.Add(videoUrl);
        return args;
    }

    /// <summary>
    /// Étape et avancement déduits d'une ligne de la sortie de yt-dlp.
    /// </summary>
    /// <param name="line">Ligne de sortie.</param>
    /// <returns>Étape (« searching », « downloading », « converting ») et pourcentage éventuel, ou null si la ligne n'apprend rien.</returns>
    public static (string Phase, int? Percent)? PhaseOf(string line)
    {
        var m = DownloadPercent.Match(line);
        if (m.Success)
        {
            return ("downloading", (int)Math.Round(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (line.StartsWith("[download] Destination", StringComparison.Ordinal))
        {
            return ("downloading", 0);
        }

        if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal) || line.StartsWith("[ffmpeg]", StringComparison.Ordinal) || line.StartsWith("[Fixup", StringComparison.Ordinal) || line.StartsWith("[Metadata]", StringComparison.Ordinal))
        {
            return ("converting", null);
        }

        if (line.StartsWith("[youtube", StringComparison.Ordinal))
        {
            return ("searching", null);
        }

        return null;
    }

    /// <summary>
    /// Arguments pour chercher un morceau par son titre et son artiste et télécharger le meilleur résultat.
    /// </summary>
    /// <param name="c">Outils.</param>
    /// <param name="track">Morceau.</param>
    /// <param name="workDir">Dossier de travail.</param>
    /// <param name="strict">Écarte les résultats dont la durée diffère de plus de 15 secondes.</param>
    /// <returns>Arguments.</returns>
    public static List<string> SearchArgs(ToolContext c, TrackSpec track, string workDir, bool strict)
    {
        var query = (track.Artist + " " + track.Title).Trim();
        var args = DownloadArgs(c, string.Empty, workDir);
        args.RemoveRange(args.Count - 2, 2);
        if (strict && track.DurationSec is > 0 and var d)
        {
            args.Add("--match-filters");
            args.Add(FormattableString.Invariant($"duration>={Math.Max(0, d - 15)} & duration<={d + 15}"));
        }

        args.Add("--max-downloads");
        args.Add("1");
        args.Add("--");
        args.Add("ytsearch5:" + query);
        return args;
    }

    /// <summary>
    /// Lit le contenu d'un lien YouTube.
    /// </summary>
    /// <param name="c">Outils.</param>
    /// <param name="link">Lien reconnu.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Contenu.</returns>
    public static async Task<Listing> ListAsync(ToolContext c, ParsedLink link, CancellationToken ct)
    {
        if (c.YtDlp is null)
        {
            throw new ImportException("yt-dlp n'est pas installé (Tableau de bord > Extensions > Media Uploader).");
        }

        var r = await ProcessRunner.RunAsync(c.YtDlp, ListArgs(c, link.Url), null, c.ChildEnvironment(), TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (r.TimedOut)
        {
            throw new ImportException("yt-dlp n'a pas répondu à temps.");
        }

        if (string.IsNullOrWhiteSpace(r.Stdout))
        {
            var why = ProcessRunner.Summarize(r.Stderr);
            throw new ImportException(why.Length > 0 ? why : $"yt-dlp a échoué (code {r.ExitCode}).");
        }

        var listing = ParseListing(r.Stdout, link);
        if (listing.Tracks.Count == 0)
        {
            throw new ImportException("Aucun morceau disponible dans ce lien (liste vide, privée ou indisponible).");
        }

        return listing;
    }

    /// <summary>
    /// Interprète la sortie JSON de yt-dlp (playlist à plat ou vidéo seule).
    /// </summary>
    /// <param name="json">Sortie de yt-dlp.</param>
    /// <param name="link">Lien d'origine.</param>
    /// <returns>Contenu.</returns>
    public static Listing ParseListing(string json, ParsedLink link)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ImportException("Réponse illisible de yt-dlp.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var web = link.Url.StartsWith("https://music.youtube.com", StringComparison.Ordinal) ? "https://music.youtube.com" : "https://www.youtube.com";
            var listing = new Listing { Kind = link.Kind };
            var entries = new List<JsonElement>();

            if (Str(root, "_type") == "playlist")
            {
                var title = Str(root, "title") ?? "Playlist";
                if (AlbumPrefix.IsMatch(title) || link.Kind == "album")
                {
                    listing.Kind = "album";
                    title = AlbumPrefix.Replace(title, string.Empty);
                }

                listing.Title = SongCount.Replace(title, string.Empty).Trim();
                listing.CoverUrl = LastThumbnail(root);
                if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    entries.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object));
                }
            }
            else
            {
                entries.Add(root);
                listing.Kind = "track";
            }

            string? albumArtist = null;
            var n = 0;
            foreach (var e in entries)
            {
                var id = Str(e, "id");
                var rawTitle = Str(e, "title");
                if (id is null || !VideoId.IsMatch(id) || rawTitle is null || rawTitle is "[Private video]" or "[Deleted video]")
                {
                    continue;
                }

                n++;
                var channel = Str(e, "channel") ?? Str(e, "uploader");
                string artist, title;
                var track = Str(e, "track");
                var artistField = Str(e, "artist") ?? FirstArtist(e);
                if (track is not null && artistField is not null)
                {
                    (artist, title) = (artistField, track);
                }
                else
                {
                    (artist, title) = TrackNaming.FromVideo(rawTitle, channel);
                }

                albumArtist ??= artist;
                var duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int?)Math.Round(d.GetDouble()) : null;
                var year = e.TryGetProperty("release_year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (int?)null;
                listing.Tracks.Add(new TrackSpec
                {
                    Id = "t" + n,
                    Title = title.Length > 0 ? title : rawTitle,
                    Artist = artist,
                    Album = listing.Kind == "album" ? null : Str(e, "album"),
                    TrackNo = listing.Kind == "album" ? n : null,
                    Year = year,
                    DurationSec = duration,
                    CoverUrl = listing.Kind == "album" ? null : SquareThumbnail(e),
                    VideoUrl = $"{web}/watch?v={id}"
                });
            }

            if (listing.Kind == "track" && listing.Tracks.Count == 1)
            {
                listing.Title = listing.Tracks[0].Title;
            }

            if (listing.Kind == "album")
            {
                // Les titres d'un album partagent son nom et son artiste.
                var tracks = listing.Tracks.ToList();
                listing.Tracks.Clear();
                foreach (var t in tracks)
                {
                    listing.Tracks.Add(new TrackSpec
                    {
                        Id = t.Id, Title = t.Title, Artist = t.Artist, AlbumArtist = albumArtist, Album = listing.Title,
                        TrackNo = t.TrackNo, Year = t.Year, DurationSec = t.DurationSec, CoverUrl = listing.CoverUrl, VideoUrl = t.VideoUrl
                    });
                }
            }

            return listing;
        }
    }

    /// <summary>
    /// Télécharge l'audio d'un morceau dans <paramref name="workDir"/>.
    /// </summary>
    /// <param name="c">Outils.</param>
    /// <param name="track">Morceau (avec son adresse de vidéo).</param>
    /// <param name="workDir">Dossier de travail vide.</param>
    /// <param name="ct">Annulation.</param>
    /// <param name="progress">Appelé quand l'étape ou le pourcentage change (facultatif).</param>
    /// <returns>Chemin du fichier audio obtenu.</returns>
    public static async Task<string> DownloadAsync(ToolContext c, TrackSpec track, string workDir, CancellationToken ct, Action<string, int?>? progress = null)
    {
        if (c.YtDlp is null)
        {
            throw new ImportException("yt-dlp n'est pas installé.");
        }

        if (track.VideoUrl is null && track.Title.Length == 0)
        {
            throw new ImportException("Adresse de vidéo manquante.");
        }

        ProcessResult r;
        string? file;
        if (track.VideoUrl is not null)
        {
            r = await RunAsync(c, DownloadArgs(c, track.VideoUrl, workDir), workDir, ct, progress).ConfigureAwait(false);
            file = FindAudio(workDir, c.Extension);
        }
        else
        {
            // Pas d'adresse (Spotify, MusicBrainz) : recherche sur YouTube, en écartant d'abord les résultats dont la durée est très éloignée (clips, versions live).
            r = await RunAsync(c, SearchArgs(c, track, workDir, strict: true), workDir, ct, progress).ConfigureAwait(false);
            file = FindAudio(workDir, c.Extension);
            if (file is null && track.DurationSec is > 0)
            {
                r = await RunAsync(c, SearchArgs(c, track, workDir, strict: false), workDir, ct, progress).ConfigureAwait(false);
                file = FindAudio(workDir, c.Extension);
            }
        }

        if (file is null)
        {
            var why = ProcessRunner.Summarize(r.Stderr);
            throw new ImportException(why.Length > 0 ? why : $"yt-dlp n'a rien produit (code {r.ExitCode}).", ProcessRunner.Tail(r.Stderr));
        }

        return file;
    }

    /// <summary>
    /// Premier fichier audio terminé d'un dossier (hors fichiers partiels).
    /// </summary>
    /// <param name="dir">Dossier.</param>
    /// <param name="extension">Extension attendue, avec le point.</param>
    /// <returns>Chemin, ou null.</returns>
    public static string? FindAudio(string dir, string extension)
    {
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0)
            : null;
    }

    private static async Task<ProcessResult> RunAsync(ToolContext c, List<string> args, string workDir, CancellationToken ct, Action<string, int?>? progress)
    {
        var r = await ProcessRunner.RunAsync(
            c.YtDlp!, args, workDir, c.ChildEnvironment(), TimeSpan.FromMinutes(15), ct,
            progress is null ? null : line => { if (PhaseOf(line) is { } p) { progress(p.Phase, p.Percent); } }).ConfigureAwait(false);
        if (r.TimedOut)
        {
            throw new ImportException("Téléchargement trop long, abandonné.");
        }

        return r;
    }

    private static void AddCommon(ToolContext c, List<string> args)
    {
        // Pause entre les requêtes internes d'un même téléchargement (page, lecteur, recherche) : une rafale de requêtes est le signe le plus net d'un robot.
        args.Add("--sleep-requests");
        args.Add("1.5");

        if (!string.IsNullOrEmpty(c.Ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(c.Ffmpeg);
        }

        if (!string.IsNullOrEmpty(c.Deno))
        {
            args.Add("--js-runtimes");
            args.Add("deno:" + c.Deno);
        }

        if (!string.IsNullOrWhiteSpace(c.CookiesPath) && File.Exists(c.CookiesPath))
        {
            args.Add("--cookies");
            args.Add(c.CookiesPath);
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    }

    private static string? FirstArtist(JsonElement e)
    {
        if (e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            var names = a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        return null;
    }

    // Miniature carrée d'une pochette d'album (YouTube Music) : les images « ytimg » sont des captures 16:9, inutilisables comme pochette.
    private static string? SquareThumbnail(JsonElement e)
    {
        if (e.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            return t.EnumerateArray().Select(x => Str(x, "url"))
                .LastOrDefault(u => u is not null && CoverHosts.Allowed(u, out var uri) && !uri!.Host.EndsWith(".ytimg.com", StringComparison.Ordinal));
        }

        return null;
    }

    private static string? LastThumbnail(JsonElement root)
    {
        if (root.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            var url = t.EnumerateArray().Select(x => Str(x, "url")).LastOrDefault(u => u is not null && u.StartsWith("https://", StringComparison.Ordinal));
            return url;
        }

        return null;
    }
}
