using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Lit les playlists, albums et titres Spotify sans clé d'API, à partir de la page d'intégration publique (« embed »).
/// Seules les métadonnées sont lues (titre, artiste, durée) : l'audio vient ensuite de YouTube, via yt-dlp.
/// Spotify ne publie que les 100 premiers titres d'une playlist dans cette page.
/// </summary>
public static class SpotifyClient
{
    /// <summary>Nombre de titres que Spotify expose dans la page d'intégration.</summary>
    public const int EmbedLimit = 100;

    private static readonly Regex NextData = new("<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// Lit le contenu d'un lien Spotify (playlist, album ou titre).
    /// </summary>
    /// <param name="link">Lien reconnu.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Contenu.</returns>
    public static async Task<Listing> ListAsync(ParsedLink link, CancellationToken ct)
    {
        string html;
        try
        {
            html = await Http.GetStringAsync($"https://open.spotify.com/embed/{link.Kind}/{link.Id}", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ImportException("Spotify est injoignable ou refuse ce lien (playlist privée ?) : " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImportException("Spotify n'a pas répondu à temps : réessayez dans un instant.");
        }

        var listing = ParseEmbed(html, link);
        if (listing.Tracks.Count == 0)
        {
            throw new ImportException("Aucun morceau trouvé dans ce lien (playlist privée ou vide ?).");
        }

        return listing;
    }

    /// <summary>
    /// Interprète la page d'intégration d'une ressource Spotify.
    /// </summary>
    /// <param name="html">Page.</param>
    /// <param name="link">Lien d'origine.</param>
    /// <returns>Contenu.</returns>
    public static Listing ParseEmbed(string html, ParsedLink link)
    {
        var m = NextData.Match(html);
        if (!m.Success)
        {
            throw new ImportException("Page Spotify inattendue : le format a peut-être changé.");
        }

        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        if (!Walk(doc.RootElement, "props", "pageProps", "state", "data", "entity", out var entity))
        {
            throw new ImportException("Ce lien Spotify n'est pas lisible (playlist privée ?).");
        }

        var name = Str(entity, "title") ?? Str(entity, "name") ?? "Spotify";
        var cover = Walk(entity, "coverArt", "sources", out var sources) && sources.ValueKind == JsonValueKind.Array
            ? sources.EnumerateArray().Select(s => Str(s, "url")).LastOrDefault(u => u is not null && u.StartsWith("https://", StringComparison.Ordinal))
            : null;
        var listing = new Listing { Kind = link.Kind, Title = name, CoverUrl = cover };
        var isAlbum = link.Kind == "album";
        var albumArtist = isAlbum ? Str(entity, "subtitle") : null;

        if (entity.TryGetProperty("trackList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var t in list.EnumerateArray())
            {
                var title = Str(t, "title");
                if (title is null || (Str(t, "entityType") is { } kind && kind != "track"))
                {
                    continue;
                }

                n++;
                listing.Tracks.Add(new TrackSpec
                {
                    Id = "t" + n,
                    Title = title,
                    Artist = (Str(t, "subtitle") ?? string.Empty).Replace(' ', ' '),
                    AlbumArtist = albumArtist,
                    Album = isAlbum ? name : null,
                    TrackNo = isAlbum ? n : null,
                    DurationSec = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)Math.Round(d.GetDouble() / 1000) : null,
                    CoverUrl = isAlbum ? cover : null
                });
            }
        }
        else if (link.Kind == "track" && Str(entity, "title") is { } single)
        {
            var artists = entity.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(x => x is not null))
                : string.Empty;
            listing.Tracks.Add(new TrackSpec
            {
                Id = "t1",
                Title = single,
                Artist = artists,
                DurationSec = entity.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)Math.Round(d.GetDouble() / 1000) : null,
                CoverUrl = cover
            });
        }

        if (listing.Kind == "playlist" && listing.Tracks.Count >= EmbedLimit)
        {
            listing.Note = $"Spotify ne publie que les {EmbedLimit} premiers titres d'une playlist : si elle en contient davantage, la suite n'est pas listée.";
        }

        return listing;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        return http;
    }

    private static bool Walk(JsonElement e, string a, string b, out JsonElement result) => Walk(e, new[] { a, b }, out result);

    private static bool Walk(JsonElement e, string a, string b, string c, string d, string f, out JsonElement result) => Walk(e, new[] { a, b, c, d, f }, out result);

    private static bool Walk(JsonElement e, string[] path, out JsonElement result)
    {
        result = e;
        foreach (var p in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(p, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Str(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    }
}
