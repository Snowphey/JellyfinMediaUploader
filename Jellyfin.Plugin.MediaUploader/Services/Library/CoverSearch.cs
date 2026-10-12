using System.Text.Json;
using Jellyfin.Plugin.MediaUploader.Services.Import;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Une pochette proposée pour un album.</summary>
/// <param name="Source">"musicbrainz" (Cover Art Archive) ou "itunes".</param>
/// <param name="Id">Identifiant chez la source (MBID du groupe de sorties, ou identifiant iTunes).</param>
/// <param name="Title">Titre de l'album.</param>
/// <param name="Artist">Artiste.</param>
/// <param name="Year">Année de sortie.</param>
/// <param name="Thumb">Adresse d'une vignette.</param>
/// <param name="Full">Adresse de l'image à enregistrer.</param>
/// <param name="Fallback">Adresse de repli si l'image principale n'existe pas (null : aucune).</param>
public sealed record CoverCandidate(string Source, string Id, string Title, string Artist, string? Year, string Thumb, string Full, string? Fallback);

/// <summary>
/// Recherche de pochettes d'album : Cover Art Archive (via MusicBrainz) et iTunes, deux sources ouvertes sans clé ni compte.
/// </summary>
public static class CoverSearch
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// Cherche des pochettes pour un album.
    /// </summary>
    /// <param name="artist">Artiste (facultatif).</param>
    /// <param name="album">Album.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Propositions (celles de Cover Art Archive dont l'image existe, puis celles d'iTunes).</returns>
    public static async Task<List<CoverCandidate>> SearchAsync(string? artist, string album, CancellationToken ct)
    {
        var found = new List<CoverCandidate>();

        // Les deux sources sont interrogées en parallèle ; l'échec de l'une n'empêche pas l'autre.
        var mb = Task.Run(async () =>
        {
            try
            {
                var candidates = ParseMusicBrainz(await MusicBrainzClient.SearchReleaseGroupsAsync(artist, album, ct).ConfigureAwait(false));
                var checks = await Task.WhenAll(candidates.Select(async c => (c, ok: await ExistsAsync(c.Thumb, ct).ConfigureAwait(false)))).ConfigureAwait(false);
                return checks.Where(x => x.ok).Select(x => x.c).ToList();
            }
            catch (Exception ex) when (ex is ImportException or HttpRequestException or TaskCanceledException or JsonException)
            {
                return new List<CoverCandidate>();
            }
        }, ct);
        var itunes = Task.Run(async () =>
        {
            try
            {
                var term = ((artist ?? string.Empty) + " " + album).Trim();
                using var response = await Http.GetAsync("https://itunes.apple.com/search?entity=album&limit=8&term=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? ParseItunes(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : new List<CoverCandidate>();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new List<CoverCandidate>();
            }
        }, ct);

        found.AddRange(await mb.ConfigureAwait(false));
        found.AddRange(await itunes.ConfigureAwait(false));
        return found;
    }

    /// <summary>
    /// Interprète une recherche de groupes de sorties MusicBrainz.
    /// </summary>
    /// <param name="json">Réponse MusicBrainz.</param>
    /// <returns>Propositions (l'existence de l'image n'est pas encore vérifiée).</returns>
    public static List<CoverCandidate> ParseMusicBrainz(string json)
    {
        var list = new List<CoverCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var g in groups.EnumerateArray().Take(8))
        {
            var id = Str(g, "id");
            var title = Str(g, "title");
            if (id is null || title is null || !Guid.TryParse(id, out _))
            {
                continue;
            }

            var artist = string.Empty;
            if (g.TryGetProperty("artist-credit", out var credit) && credit.ValueKind == JsonValueKind.Array)
            {
                artist = string.Concat(credit.EnumerateArray().Select(c => (Str(c, "name") ?? Str(c.TryGetProperty("artist", out var a) ? a : default, "name") ?? string.Empty) + (Str(c, "joinphrase") ?? string.Empty)));
            }

            var date = Str(g, "first-release-date");
            list.Add(new CoverCandidate("musicbrainz", id, title, artist.Trim(), date is { Length: >= 4 } ? date[..4] : null, $"https://coverartarchive.org/release-group/{id}/front-250", $"https://coverartarchive.org/release-group/{id}/front-1200", $"https://coverartarchive.org/release-group/{id}/front-500"));
        }

        return list;
    }

    /// <summary>
    /// Interprète une recherche d'albums iTunes.
    /// </summary>
    /// <param name="json">Réponse d'iTunes.</param>
    /// <returns>Propositions.</returns>
    public static List<CoverCandidate> ParseItunes(string json)
    {
        var list = new List<CoverCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in results.EnumerateArray().Take(8))
        {
            var art = Str(r, "artworkUrl100");
            var title = Str(r, "collectionName");
            if (art is null || title is null || !CoverHosts.Allowed(art, out _))
            {
                continue;
            }

            var id = r.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : title;
            var date = Str(r, "releaseDate");
            list.Add(new CoverCandidate("itunes", id, title, Str(r, "artistName") ?? string.Empty, date is { Length: >= 4 } ? date[..4] : null, art.Replace("100x100bb", "300x300bb"), art.Replace("100x100bb", "1000x1000bb"), null));
        }

        return list;
    }

    private static async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.Plugin);
        return http;
    }
}
