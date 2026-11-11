using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Artistes d'un morceau selon MusicBrainz.</summary>
/// <param name="Artists">Interprètes du morceau, séparés (noms en écriture latine quand MusicBrainz en connaît une).</param>
/// <param name="AlbumArtist">Artiste de l'album (« Lin-Manuel Miranda &amp; Germaine Franco », « Various Artists »), ou null.</param>
/// <param name="Release">Titre de la sortie retenue.</param>
public sealed record MbCredit(IReadOnlyList<string> Artists, string? AlbumArtist, string? Release)
{
    /// <summary>Gets les noms tels qu'écrits sur MusicBrainz (avant conversion en écriture latine), pour reconnaître ceux des tags.</summary>
    public IReadOnlyList<string> OriginalNames { get; init; } = Array.Empty<string>();
}

/// <summary>Correspondance brute trouvée dans une recherche d'enregistrements (avant la conversion des noms en écriture latine).</summary>
/// <param name="Artists">Interprètes : nom et identifiant MusicBrainz.</param>
/// <param name="AlbumCredit">Artistes de la sortie : nom, identifiant et liaison (« &amp; », « , »).</param>
/// <param name="Release">Titre de la sortie.</param>
public sealed record MbMatch(IReadOnlyList<(string Name, string? Id)> Artists, IReadOnlyList<(string Name, string? Id, string Join)> AlbumCredit, string? Release);

/// <summary>
/// Demande à MusicBrainz qui interprète un morceau, et à qui est l'album. Une réponse n'est retenue que si elle est sûre :
/// même titre, au moins un artiste en commun avec les tags. Les réponses (même « rien trouvé ») sont gardées en mémoire 7 jours.
/// </summary>
public static class MusicBrainzCredits
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, (DateTime At, MbCredit? Credit)> Cache = new();
    private static readonly ConcurrentDictionary<string, string?> LatinNames = new();

    /// <summary>
    /// Clé de cache d'un morceau.
    /// </summary>
    /// <param name="title">Titre.</param>
    /// <param name="credit">Mention d'artistes.</param>
    /// <param name="album">Album.</param>
    /// <returns>Clé.</returns>
    public static string CacheKey(string? title, string? credit, string? album) => NameTools.TitleKey(title) + "|" + NameTools.TitleKey(credit) + "|" + NameTools.TitleKey(album);

    /// <summary>
    /// Réponse déjà connue (sans requête).
    /// </summary>
    /// <param name="title">Titre.</param>
    /// <param name="credit">Mention.</param>
    /// <param name="album">Album.</param>
    /// <param name="credit2">Réponse (null : rien trouvé).</param>
    /// <returns>Vrai si la question a déjà été posée.</returns>
    public static bool TryGetCached(string? title, string? credit, string? album, out MbCredit? credit2)
    {
        if (Cache.TryGetValue(CacheKey(title, credit, album), out var hit) && DateTime.UtcNow - hit.At < Ttl)
        {
            credit2 = hit.Credit;
            return true;
        }

        credit2 = null;
        return false;
    }

    /// <summary>
    /// Cherche le morceau sur MusicBrainz (une requête, plus une par artiste au nom non latin non encore connu).
    /// </summary>
    /// <param name="title">Titre (tags).</param>
    /// <param name="credit">Mention d'artistes (tags).</param>
    /// <param name="album">Album (tags), pour choisir la bonne sortie.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Réponse, ou null si rien de sûr.</returns>
    public static async Task<MbCredit?> LookupAsync(string title, string credit, string? album, CancellationToken ct)
    {
        if (TryGetCached(title, credit, album, out var cached))
        {
            return cached;
        }

        var lead = ArtistCredits.Split(credit).Lead;
        var query = $"recording:\"{MusicBrainzClient.EscapeLucene(title)}\" AND artist:\"{MusicBrainzClient.EscapeLucene(lead)}\"";
        var json = await MusicBrainzClient.GetJsonAsync($"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=15", ct).ConfigureAwait(false);
        var match = ParseRecordings(json, title, credit, album);

        // L'artiste principal des tags n'a pas toujours le même nom sur MusicBrainz (« Carolina Gaitán - La Gaita ») : le titre seul, filtré ensuite.
        if (match is null)
        {
            var byTitle = $"recording:\"{MusicBrainzClient.EscapeLucene(title)}\"";
            json = await MusicBrainzClient.GetJsonAsync($"recording?query={Uri.EscapeDataString(byTitle)}&fmt=json&limit=25", ct).ConfigureAwait(false);
            match = ParseRecordings(json, title, credit, album);
        }
        MbCredit? result = null;
        if (match is not null)
        {
            async Task<string> Latin(string name, string? id)
            {
                if (id is null || !ArtistCredits.HasNonLatin(name))
                {
                    return name;
                }

                if (!LatinNames.TryGetValue(id, out var latin))
                {
                    var artistJson = await MusicBrainzClient.GetJsonAsync($"artist/{Uri.EscapeDataString(id)}?inc=aliases&fmt=json", ct).ConfigureAwait(false);
                    latin = ParseLatinAlias(artistJson);
                    LatinNames[id] = latin;
                }

                return latin ?? name;
            }

            var artists = new List<string>();
            foreach (var (name, id) in match.Artists)
            {
                var n = await Latin(name, id).ConfigureAwait(false);
                if (!artists.Any(a => ArtistCredits.Key(a) == ArtistCredits.Key(n)))
                {
                    artists.Add(n);
                }
            }

            string? albumArtist = null;
            if (match.AlbumCredit.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (name, id, join) in match.AlbumCredit)
                {
                    sb.Append(await Latin(name, id).ConfigureAwait(false)).Append(join);
                }

                albumArtist = sb.ToString().Trim();
            }

            result = artists.Count > 0 ? new MbCredit(artists, albumArtist, match.Release) { OriginalNames = match.Artists.Select(a => a.Name).ToList() } : null;
        }

        Cache[CacheKey(title, credit, album)] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Choisit, dans une recherche d'enregistrements, celui qui correspond au morceau (même titre, un artiste en commun),
    /// et sa sortie : celle dont le titre est l'album des tags, sinon un album ou single d'origine plutôt qu'une compilation.
    /// </summary>
    /// <param name="json">Réponse de MusicBrainz.</param>
    /// <param name="title">Titre (tags).</param>
    /// <param name="credit">Mention d'artistes (tags).</param>
    /// <param name="album">Album (tags).</param>
    /// <returns>Correspondance, ou null.</returns>
    public static MbMatch? ParseRecordings(string json, string title, string credit, string? album)
    {
        if (JsonNode.Parse(json)?["recordings"] is not JsonArray recordings)
        {
            return null;
        }

        var wantTitle = DuplicateMatcher.Key(credit, title);
        if (wantTitle is null)
        {
            return null;
        }

        var creditKeys = ArtistCredits.Split(credit).Artists.Select(ArtistCredits.Key).ToHashSet(StringComparer.Ordinal);
        var albumKey = AlbumKey(album);

        // Tous les enregistrements qui correspondent (titre, un artiste en commun) sont comparés : le même morceau figure souvent
        // sur des compilations (« Disney Hits »), on préfère la sortie qui porte le nom de l'album des tags, sinon une sortie d'origine.
        MbMatch? best = null;
        var bestScore = -1;
        foreach (var rec in recordings.OfType<JsonObject>())
        {
            var names = Credit(rec["artist-credit"]);
            var overlap = names.Count(n => creditKeys.Contains(ArtistCredits.Key(n.Name))
                || creditKeys.Any(k => k.Length > 3 && ArtistCredits.Key(n.Name).Length > 3 && (ArtistCredits.Key(n.Name).Contains(k, StringComparison.Ordinal) || k.Contains(ArtistCredits.Key(n.Name), StringComparison.Ordinal))));
            if (overlap == 0)
            {
                continue;
            }

            var recTitle = rec["title"]?.GetValue<string>() ?? string.Empty;
            var theirs = DuplicateMatcher.Key(string.Join(", ", names.Select(n => n.Name)), recTitle);
            // Titre comparé sans les mentions descriptives (« Annihilate (Spider-Man: Across the Spider-Verse) » / « Annihilate ») ; une autre version (live, remix…) reste écartée.
            if (theirs is null || !(DuplicateMatcher.SameTitle(wantTitle, theirs) || (wantTitle.Title == theirs.Title && wantTitle.Markers == theirs.Markers)))
            {
                continue;
            }

            var releases = (rec["releases"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            bool Matches(JsonObject r)
            {
                var t = AlbumKey(r["title"]?.GetValue<string>());
                return albumKey.Length > 0 && t.Length > 3 && (t == albumKey || t.Contains(albumKey, StringComparison.Ordinal) || albumKey.Contains(t, StringComparison.Ordinal));
            }

            // Une sortie d'origine portant le nom de l'album gagne ; une sortie d'origine quelconque passe avant une compilation,
            // même si celle-ci porte le nom de l'album (« We Don't Talk About Bruno: As Heard Around the World »).
            int ReleaseScore(JsonObject r)
            {
                var comp = IsCompilation(r);
                var exact = AlbumKey(r["title"]?.GetValue<string>()) == albumKey;
                return (Matches(r) ? (comp ? 25 : 100) : 0) + (exact && !comp ? 5 : 0) + (comp ? 0 : 30) + (r["status"]?.GetValue<string>() is null or "Official" ? 1 : 0);
            }

            var release = releases.OrderByDescending(ReleaseScore).FirstOrDefault();
            var score = (release is null ? 0 : ReleaseScore(release)) + overlap;
            if (score <= bestScore)
            {
                continue;
            }

            var albumCredit = release?["artist-credit"] is JsonArray rc ? CreditWithJoins(rc) : CreditWithJoins(rec["artist-credit"] as JsonArray);
            best = new MbMatch(names.Select(n => (n.Name, n.Id)).ToList(), albumCredit, release?["title"]?.GetValue<string>());
            bestScore = score;
        }

        return best;
    }

    /// <summary>
    /// Nom en écriture latine d'un artiste, d'après ses alias MusicBrainz (alias principal anglais ou latin, sinon tout alias latin).
    /// </summary>
    /// <param name="json">Réponse « artist?inc=aliases ».</param>
    /// <returns>Nom latin, ou null.</returns>
    public static string? ParseLatinAlias(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        var name = node?["name"]?.GetValue<string>();
        if (name is not null && !ArtistCredits.HasNonLatin(name))
        {
            return name;
        }

        var aliases = (node?["aliases"] as JsonArray)?.OfType<JsonObject>()
            .Select(a => (Name: a["name"]?.GetValue<string>(), Locale: a["locale"]?.GetValue<string>(), Primary: a["primary"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb, Type: a["type"]?.GetValue<string>()))
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !ArtistCredits.HasNonLatin(a.Name))
            .ToList() ?? new();
        var best = aliases.FirstOrDefault(a => a.Primary && (a.Locale is "en" || a.Locale?.EndsWith("-Latn", StringComparison.Ordinal) == true))
            .Name
            ?? aliases.FirstOrDefault(a => a.Locale is "en").Name
            ?? aliases.FirstOrDefault(a => a.Type is "Artist name" or "Legal name").Name
            ?? aliases.FirstOrDefault(a => a.Type is not "Search hint").Name;
        return best;
    }

    // Titre d'album comparable : sans les parenthèses (« Encanto (Original Motion Picture Soundtrack) » et « Encanto » se valent).
    private static string AlbumKey(string? album) => NameTools.TitleKey(System.Text.RegularExpressions.Regex.Replace(album ?? string.Empty, @"[\(\[][^\)\]]*[\)\]]", " "));

    private static bool IsCompilation(JsonObject release)
    {
        var secondary = release["release-group"]?["secondary-types"] as JsonArray;
        var various = (release["artist-credit"] as JsonArray)?.OfType<JsonObject>().Any(c => c["name"]?.GetValue<string>() == ArtistCredits.VariousArtists) == true;
        return various || secondary?.Any(t => t?.GetValue<string>() is "Compilation" or "DJ-mix" or "Mixtape/Street") == true;
    }

    private static List<(string Name, string? Id)> Credit(JsonNode? node) =>
        (node as JsonArray)?.OfType<JsonObject>()
            .Select(c => (Name: c["name"]?.GetValue<string>() ?? string.Empty, Id: c["artist"]?["id"]?.GetValue<string>()))
            .Where(c => c.Name.Length > 0)
            .ToList() ?? new();

    private static List<(string Name, string? Id, string Join)> CreditWithJoins(JsonArray? node) =>
        node?.OfType<JsonObject>()
            .Select(c => (Name: c["name"]?.GetValue<string>() ?? string.Empty, Id: c["artist"]?["id"]?.GetValue<string>(), Join: c["joinphrase"]?.GetValue<string>() ?? string.Empty))
            .Where(c => c.Name.Length > 0)
            .ToList() ?? new();
}
