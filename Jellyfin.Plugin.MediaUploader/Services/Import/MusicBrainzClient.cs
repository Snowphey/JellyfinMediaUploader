using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Un artiste trouvé dans MusicBrainz.</summary>
/// <param name="Id">Identifiant MusicBrainz (MBID).</param>
/// <param name="Name">Nom.</param>
/// <param name="Disambiguation">Précision (« rappeur français »…).</param>
/// <param name="Country">Pays.</param>
/// <param name="Type">Personne, groupe…</param>
public sealed record ArtistHit(string Id, string Name, string? Disambiguation, string? Country, string? Type);

/// <summary>Un album, EP ou single (groupe de sorties) d'un artiste.</summary>
/// <param name="Id">Identifiant du groupe de sorties.</param>
/// <param name="Title">Titre.</param>
/// <param name="Type">Album, EP, Single…</param>
/// <param name="Extras">Types secondaires (Live, Compilation, Remix…).</param>
/// <param name="Date">Date de première sortie.</param>
public sealed record AlbumHit(string Id, string Title, string Type, IReadOnlyList<string> Extras, string? Date);

/// <summary>
/// Catalogue d'artistes et d'albums via MusicBrainz et Cover Art Archive : données ouvertes, sans clé ni compte.
/// Les pistes d'un album servent ensuite à retrouver chaque morceau sur YouTube Music (yt-dlp).
/// </summary>
public static class MusicBrainzClient
{
    private const string Api = "https://musicbrainz.org/ws/2/";
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime At, int Count)> TrackCounts = new();

    /// <summary>
    /// Cherche des artistes par nom.
    /// </summary>
    /// <param name="query">Texte saisi.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Artistes, du plus pertinent au moins pertinent.</returns>
    public static async Task<List<ArtistHit>> SearchArtistsAsync(string query, CancellationToken ct)
    {
        var json = await GetAsync($"artist?query={Uri.EscapeDataString(EscapeLucene(query))}&fmt=json&limit=12", ct).ConfigureAwait(false);
        return ParseArtists(json);
    }

    /// <summary>
    /// Liste les albums, EP et singles d'un artiste.
    /// </summary>
    /// <param name="artistId">MBID de l'artiste.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Sorties, les plus récentes d'abord.</returns>
    public static async Task<List<AlbumHit>> ArtistAlbumsAsync(string artistId, CancellationToken ct)
    {
        if (!Guid.TryParse(artistId, out var id))
        {
            throw new ImportException("Identifiant d'artiste invalide.");
        }

        var all = new List<AlbumHit>();
        for (var offset = 0; offset < 400; offset += 100)
        {
            var json = await GetAsync($"release-group?artist={id}&type=album%7Cep%7Csingle&fmt=json&limit=100&offset={offset}", ct).ConfigureAwait(false);
            var (items, total) = ParseReleaseGroups(json);
            all.AddRange(items);
            if (offset + 100 >= total || items.Count == 0)
            {
                break;
            }
        }

        return all.OrderByDescending(a => a.Date ?? string.Empty, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Nombre de pistes d'un album (celui de la sortie la plus représentative du groupe, comme pour l'import). Gardé 24 h.
    /// </summary>
    /// <param name="releaseGroupId">Identifiant du groupe de sorties.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Nombre de pistes (0 si MusicBrainz n'a aucune sortie officielle détaillée).</returns>
    public static async Task<int> AlbumTrackCountAsync(string releaseGroupId, CancellationToken ct)
    {
        if (!Guid.TryParse(releaseGroupId, out var id))
        {
            throw new ImportException("Identifiant d'album invalide.");
        }

        if (TrackCounts.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(24))
        {
            return hit.Count;
        }

        var json = await GetAsync($"release?release-group={id}&inc=media&status=official&fmt=json&limit=25", ct).ConfigureAwait(false);
        var count = ParseTrackCount(json);
        TrackCounts[id] = (DateTime.UtcNow, count);
        return count;
    }

    /// <summary>
    /// Nombre de pistes de la sortie la plus représentative d'une liste de sorties (le nombre le plus courant ; à égalité, le plus petit : les éditions deluxe en ont plus).
    /// </summary>
    /// <param name="json">Réponse MusicBrainz (sorties avec leurs supports).</param>
    /// <returns>Nombre de pistes, ou 0.</returns>
    public static int ParseTrackCount(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ImportException("Réponse MusicBrainz illisible.");
        }

        var counts = (root?["releases"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(r => (r["media"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Sum(m => Int(m, "track-count") ?? (m["tracks"] as JsonArray)?.Count ?? 0))
            .Where(c => c > 0)
            .ToList();
        return counts.Count == 0 ? 0 : counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    }

    /// <summary>
    /// Reconstitue la liste des pistes d'un album (la sortie la plus représentative du groupe) .
    /// </summary>
    /// <param name="releaseGroupId">Identifiant du groupe de sorties.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Album et ses pistes.</returns>
    public static async Task<Listing> AlbumListingAsync(string releaseGroupId, CancellationToken ct)
    {
        if (!Guid.TryParse(releaseGroupId, out var id))
        {
            throw new ImportException("Identifiant d'album invalide.");
        }

        string releases;
        try
        {
            releases = await GetAsync($"release?release-group={id}&inc=recordings%2Bartist-credits%2Bisrcs&status=official&fmt=json&limit=25", ct).ConfigureAwait(false);
        }
        catch (ImportException)
        {
            // Certaines combinaisons d'« inc » sont refusées : on retente sans les ISRC (le titre et la durée suffisent à la recherche).
            releases = await GetAsync($"release?release-group={id}&inc=recordings%2Bartist-credits&status=official&fmt=json&limit=25", ct).ConfigureAwait(false);
        }

        var cover = $"https://coverartarchive.org/release-group/{id}/front-500";
        var listing = ParseRelease(releases, id.ToString(), await CoverExistsAsync(cover, ct).ConfigureAwait(false) ? cover : null);
        if (listing.Tracks.Count == 0)
        {
            throw new ImportException("Aucune sortie officielle avec pistes pour cet album dans MusicBrainz.");
        }

        return listing;
    }

    /// <summary>
    /// Interprète la liste des sorties d'un groupe et retient la plus représentative.
    /// </summary>
    /// <param name="json">Réponse MusicBrainz.</param>
    /// <param name="releaseGroupId">Identifiant du groupe.</param>
    /// <param name="coverUrl">Pochette (null si absente).</param>
    /// <returns>Album et pistes.</returns>
    public static Listing ParseRelease(string json, string releaseGroupId, string? coverUrl)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        var candidates = (root?["releases"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(r => (Release: r, Count: TrackCount(r)))
            .Where(x => x.Count > 0)
            .ToList();
        var listing = new Listing { Kind = "album", CoverUrl = coverUrl };
        if (candidates.Count == 0)
        {
            return listing;
        }

        // Les éditions deluxe ont plus de pistes que l'album d'origine : on prend le nombre de pistes le plus courant, puis la sortie la plus ancienne.
        var common = candidates.GroupBy(x => x.Count).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var best = candidates.Where(x => x.Count == common).OrderBy(x => string.IsNullOrEmpty(Str(x.Release, "date")) ? "9999" : Str(x.Release, "date"), StringComparer.Ordinal).First().Release;

        var album = Str(best, "title") ?? "Album";
        var albumArtist = CreditNames(best["artist-credit"], out _);
        var date = Str(best, "date");
        var year = YearOf(date);
        var media = (best["media"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var n = 0;
        foreach (var medium in media)
        {
            var disc = medium["position"]?.GetValue<int>() ?? (media.IndexOf(medium) + 1);
            var tracks = (medium["tracks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            foreach (var t in tracks)
            {
                var title = Str(t, "title") ?? Str(t["recording"] as JsonObject, "title");
                if (title is null)
                {
                    continue;
                }

                n++;
                var recording = t["recording"] as JsonObject;
                var artist = CreditNames(t["artist-credit"] ?? recording?["artist-credit"], out _);
                if (artist.Length == 0)
                {
                    artist = albumArtist;
                }

                var lengthMs = Int(t, "length") ?? Int(recording, "length") ?? 0;
                var number = Int(t, "position") ?? n;
                listing.Tracks.Add(new TrackSpec
                {
                    Id = "t" + n,
                    Title = title,
                    Artist = artist,
                    AlbumArtist = albumArtist,
                    Album = album,
                    TrackNo = number,
                    DiscNo = disc,
                    Year = year,
                    DurationSec = lengthMs > 0 ? lengthMs / 1000 : null,
                    CoverUrl = coverUrl
                });
            }
        }

        listing.Title = album;
        return listing;
    }

    /// <summary>
    /// Interprète une recherche d'artistes.
    /// </summary>
    /// <param name="json">Réponse MusicBrainz.</param>
    /// <returns>Artistes.</returns>
    public static List<ArtistHit> ParseArtists(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        return (root?["artists"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(a => Str(a, "id") is not null && Str(a, "name") is not null)
            .Select(a => new ArtistHit(Str(a, "id")!, Str(a, "name")!, Str(a, "disambiguation"), Str(a, "country"), Str(a, "type")))
            .ToList();
    }

    /// <summary>
    /// Interprète une page de groupes de sorties.
    /// </summary>
    /// <param name="json">Réponse MusicBrainz.</param>
    /// <returns>Sorties de la page et nombre total.</returns>
    public static (List<AlbumHit> Items, int Total) ParseReleaseGroups(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        var items = (root?["release-groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(g => Str(g, "id") is not null && Str(g, "title") is not null)
            .Select(g => new AlbumHit(
                Str(g, "id")!,
                Str(g, "title")!,
                Str(g, "primary-type") ?? "Autre",
                (g["secondary-types"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList(),
                Str(g, "first-release-date")))
            .ToList();
        return (items, Int(root, "release-group-count") ?? items.Count);
    }

    /// <summary>
    /// Échappe les caractères spéciaux de la syntaxe de recherche (Lucene) de MusicBrainz.
    /// </summary>
    /// <param name="text">Texte saisi.</param>
    /// <returns>Texte sûr.</returns>
    public static string EscapeLucene(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if ("+-&|!(){}[]^\"~*?:\\/".IndexOf(c, StringComparison.Ordinal) >= 0)
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int TrackCount(JsonObject release)
    {
        return (release["media"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Sum(m => (m["tracks"] as JsonArray)?.Count ?? 0);
    }

    // « A feat. B » : noms tels que crédités, avec leurs mots de liaison ; la liste des artistes sert aux tags.
    private static string CreditNames(JsonNode? credit, out List<string> names)
    {
        names = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in (credit as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = Str(c, "name") ?? Str(c["artist"] as JsonObject, "name");
            if (name is null)
            {
                continue;
            }

            names.Add(name);
            sb.Append(name).Append(Str(c, "joinphrase") ?? string.Empty);
        }

        return sb.ToString().Trim();
    }

    private static int? YearOf(string? date)
    {
        return date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) && y > 0 ? y : null;
    }

    private static string? Str(JsonObject? o, string name)
    {
        return o?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static int? Int(JsonObject? o, string name)
    {
        return o?[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinMediaUploader/1.2 (+https://github.com/Snowphey/JellyfinMediaUploader)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // MusicBrainz demande au plus une requête par seconde : toutes passent par une file qui espace les appels.
    private static async Task<string> GetAsync(string relative, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _last + TimeSpan.FromMilliseconds(1100) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                HttpResponseMessage response;
                try
                {
                    response = await Http.GetAsync(Api + relative, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ImportException("MusicBrainz est injoignable : " + ex.Message);
                }
                finally
                {
                    _last = DateTime.UtcNow;
                }

                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 2), ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ImportException($"MusicBrainz a répondu {(int)response.StatusCode}.");
                    }

                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> CoverExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
