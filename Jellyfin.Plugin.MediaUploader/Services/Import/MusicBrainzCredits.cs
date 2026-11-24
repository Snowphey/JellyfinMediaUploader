using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Artistes d'un morceau selon MusicBrainz.</summary>
/// <param name="Artists">Interprètes du morceau, séparés (noms en écriture latine quand MusicBrainz en connaît une).</param>
/// <param name="AlbumArtist">Artiste de l'album (« Lin-Manuel Miranda &amp; Germaine Franco », « Various Artists »), ou null.</param>
/// <param name="Release">Titre de la sortie retenue.</param>
public sealed record MbCredit(IReadOnlyList<string> Artists, string? AlbumArtist, string? Release)
{
    /// <summary>Gets le numéro de piste sur la sortie retenue (0 : inconnu).</summary>
    public int Track { get; init; }

    /// <summary>Gets le numéro de disque sur la sortie retenue (0 : inconnu).</summary>
    public int Disc { get; init; }

    /// <summary>Gets l'année de la sortie retenue (0 : inconnue).</summary>
    public int Year { get; init; }

    /// <summary>
    /// Gets l'artiste du dossier : celui de la sortie quand elle est créditée à d'autres (compositeurs d'une bande originale),
    /// l'artiste principal quand elle l'est aux interprètes eux-mêmes (« disiz &amp; Theodora » : « disiz »).
    /// </summary>
    public string FolderArtist
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AlbumArtist) || AlbumArtist == ArtistCredits.VariousArtists)
            {
                return ArtistCredits.FolderArtist(Artists);
            }

            var mine = Artists.Concat(OriginalNames).Select(ArtistCredits.Key).ToHashSet(StringComparer.Ordinal);
            return ArtistCredits.Candidates(AlbumArtist).All(a => mine.Contains(ArtistCredits.Key(a))) ? ArtistCredits.FolderArtist(Artists) : AlbumArtist;
        }
    }

    /// <summary>Gets les noms tels qu'écrits sur MusicBrainz (avant conversion en écriture latine), pour reconnaître ceux des tags.</summary>
    public IReadOnlyList<string> OriginalNames { get; init; } = Array.Empty<string>();
}

/// <summary>Correspondance brute trouvée dans une recherche d'enregistrements (avant la conversion des noms en écriture latine).</summary>
/// <param name="Artists">Interprètes : nom et identifiant MusicBrainz.</param>
/// <param name="AlbumCredit">Artistes de la sortie : nom, identifiant et liaison (« &amp; », « , »).</param>
/// <param name="Release">Titre de la sortie.</param>
public sealed record MbMatch(IReadOnlyList<(string Name, string? Id)> Artists, IReadOnlyList<(string Name, string? Id, string Join)> AlbumCredit, string? Release)
{
    /// <summary>Gets le numéro de piste sur la sortie (0 : inconnu).</summary>
    public int Track { get; init; }

    /// <summary>Gets le numéro de disque (0 : inconnu).</summary>
    public int Disc { get; init; }

    /// <summary>Gets l'année de la sortie (0 : inconnue).</summary>
    public int Year { get; init; }
}

/// <summary>
/// Demande à MusicBrainz qui interprète un morceau, et à qui est l'album. Une réponse n'est retenue que si elle est sûre :
/// même titre, au moins un artiste en commun avec les tags. Les réponses (même « rien trouvé ») sont gardées en mémoire 7 jours.
/// </summary>
public static class MusicBrainzCredits
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, (DateTime At, MbCredit? Credit)> Cache = new();
    private static readonly ConcurrentDictionary<string, string?> LatinNames = new();

    // Mentions d'un titre YouTube ou Spotify que MusicBrainz n'écrit pas : invités entre parenthèses, « - Mono », « - Remastered 2011 ».
    private static readonly System.Text.RegularExpressions.Regex TitleNoise = new(
        @"\s*[\(\[](?:[^\)\]]*[\s,])?(?:feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]|\s*[\(\[](?:with|avec)\s[^\)\]]*[\)\]]|\s+-\s+(?:mono|stereo|(?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?(?:\s+version)?|single version|radio edit)\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Sorties qui ne sont pas l'album d'origine : live, remix, compilation…
    private static readonly HashSet<string> NotOriginal = new(StringComparer.OrdinalIgnoreCase) { "Live", "Remix", "Compilation", "DJ-mix", "Mixtape/Street", "Demo", "Interview", "Spokenword", "Audio drama", "Field recording" };

    // Enregistrements d'une autre version (« live, 2009-05-21 », « remix »), écartés sauf si le titre cherché le demande.
    private static readonly System.Text.RegularExpressions.Regex OtherVersion = new(@"\b(live|demo|remix|acoustic|instrumental|karaoke|a cappella|edit)\b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Titre tel que MusicBrainz l'écrit probablement : sans les invités entre parenthèses ni « - Mono » / « - Remastered ».
    /// </summary>
    /// <param name="title">Titre (tags).</param>
    /// <returns>Titre nettoyé (le titre d'origine s'il ne reste rien).</returns>
    public static string SearchTitle(string title)
    {
        var clean = TitleNoise.Replace(title, string.Empty).Trim();
        return clean.Length > 0 ? clean : title.Trim();
    }

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

        var lead = ArtistCredits.Candidates(credit).FirstOrDefault() ?? credit;
        var cacheTitle = title;
        title = SearchTitle(title);
        var query = $"recording:\"{MusicBrainzClient.EscapeLucene(title)}\" AND artist:\"{MusicBrainzClient.EscapeLucene(lead)}\"";
        var json = await MusicBrainzClient.GetJsonAsync($"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=100", ct).ConfigureAwait(false);
        var match = ParseRecordings(json, title, credit, album);

        // L'artiste principal des tags n'a pas toujours le même nom sur MusicBrainz (« Carolina Gaitán - La Gaita ») : le titre seul, filtré ensuite.
        if (match is null)
        {
            var byTitle = $"recording:\"{MusicBrainzClient.EscapeLucene(title)}\"";
            json = await MusicBrainzClient.GetJsonAsync($"recording?query={Uri.EscapeDataString(byTitle)}&fmt=json&limit=100", ct).ConfigureAwait(false);
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

            result = artists.Count > 0 ? new MbCredit(artists, albumArtist, match.Release) { OriginalNames = match.Artists.Select(a => a.Name).ToList(), Track = match.Track, Disc = match.Disc, Year = match.Year } : null;
        }

        Cache[CacheKey(cacheTitle, credit, album)] = (DateTime.UtcNow, result);
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

        // Noms coupés partout (« A & B » : A, B) en plus du découpage prudent : MusicBrainz tranche ensuite.
        var creditKeys = ArtistCredits.Split(credit).Artists.Concat(ArtistCredits.Candidates(credit)).Select(ArtistCredits.Key).ToHashSet(StringComparer.Ordinal);
        var albumKey = AlbumKey(album);

        // Tous les enregistrements qui correspondent (titre, un artiste en commun) sont comparés : le même morceau figure souvent
        // sur des compilations (« Disney Hits »), on préfère la sortie qui porte le nom de l'album des tags, sinon une sortie d'origine.
        MbMatch? best = null;
        var bestScore = -1;
        var bestDate = string.Empty;
        var wantVersion = OtherVersion.IsMatch(title);
        foreach (var rec in recordings.OfType<JsonObject>())
        {
            // Enregistrement d'une autre version (live, remix…) ou clip : écarté, sauf si c'est ce qui est cherché.
            var disambiguation = rec["disambiguation"]?.GetValue<string>() ?? string.Empty;
            if ((!wantVersion && OtherVersion.IsMatch(disambiguation)) || rec["video"] is JsonValue v && v.TryGetValue<bool>(out var isVideo) && isVideo)
            {
                continue;
            }

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
            // Sans album connu : l'album studio d'origine (pas un live, une compilation ni un remix), un album plutôt qu'un single,
            // puis la sortie la plus ancienne.
            int ReleaseScore(JsonObject r)
            {
                var comp = IsCompilation(r) || (albumKey.Length == 0 && Foreign(r, names));
                var exact = AlbumKey(r["title"]?.GetValue<string>()) == albumKey;
                var secondary = (r["release-group"]?["secondary-types"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? string.Empty).ToList() ?? new List<string>();
                var notOriginal = secondary.Any(NotOriginal.Contains);
                var primary = r["release-group"]?["primary-type"]?.GetValue<string>() switch { "Album" => 12, "EP" => 8, "Single" => 6, _ => 0 };

                // Album inconnu : une bande originale (film, jeu) passe après l'album de l'artiste.
                var soundtrack = albumKey.Length == 0 && secondary.Contains("Soundtrack", StringComparer.OrdinalIgnoreCase) ? -15 : 0;
                return (Matches(r) ? (comp ? 25 : 100) : 0) + (exact && !comp ? 5 : 0) + (comp ? 0 : 30) + (notOriginal ? -40 : 0) + primary + soundtrack
                    + (r["status"]?.GetValue<string>() switch { null or "Official" => 1, "Bootleg" when albumKey.Length == 0 => -20, _ => 0 });
            }

            static string DateOf(JsonObject r) => r["date"]?.GetValue<string>() is { Length: >= 4 } d ? d : "9999";

            var release = releases.OrderByDescending(ReleaseScore).ThenBy(DateOf, StringComparer.Ordinal).FirstOrDefault();
            var score = (release is null ? 0 : ReleaseScore(release)) + overlap;
            var date = release is null ? "9999" : DateOf(release);
            if (score < bestScore || (score == bestScore && string.CompareOrdinal(date, bestDate) >= 0))
            {
                continue;
            }

            bestDate = date;

            // Album inconnu et seule une compilation (ou la bande originale d'un autre) trouvée : pas d'album plutôt qu'un mauvais.
            if (release is not null && albumKey.Length == 0 && !Original(release, names))
            {
                release = null;
            }

            var albumCredit = release?["artist-credit"] is JsonArray rc ? CreditWithJoins(rc) : CreditWithJoins(rec["artist-credit"] as JsonArray);
            var medium = (release?["media"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            // Numéro de piste : « 3 », ou « A3 » sur un vinyle ; 1 à défaut.
            var number = System.Text.RegularExpressions.Regex.Match((medium?["track"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["number"]?.GetValue<string>() ?? string.Empty, @"\d+").Value;
            var trackNo = int.TryParse(number, out var tn) && tn > 0 ? tn : (release is null ? 0 : 1);
            var discNo = medium?["position"] is JsonValue pos && pos.TryGetValue<int>(out var dn) ? dn : 0;
            var year = int.TryParse((release?["date"]?.GetValue<string>() ?? string.Empty).Split('-')[0], out var y) ? y : 0;
            best = new MbMatch(names.Select(n => (n.Name, n.Id)).ToList(), albumCredit, release?["title"]?.GetValue<string>()) { Track = trackNo, Disc = discNo, Year = year };
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

    // Sortie créditée à d'autres que les interprètes du morceau (bande originale de « Top Gun » : Harold Faltermeyer).
    private static bool Foreign(JsonObject release, List<(string Name, string? Id)> performers)
    {
        if (release["artist-credit"] is not JsonArray credit || credit.Count == 0)
        {
            return false;
        }

        var mine = performers.Select(p => ArtistCredits.Key(p.Name)).ToHashSet(StringComparer.Ordinal);
        return !credit.OfType<JsonObject>().Any(c => mine.Contains(ArtistCredits.Key(c["name"]?.GetValue<string>() ?? string.Empty)));
    }

    // Sortie d'origine de l'artiste : ni compilation, ni live ou remix, ni sortie d'un autre.
    private static bool Original(JsonObject release, List<(string Name, string? Id)> performers)
    {
        var secondary = (release["release-group"]?["secondary-types"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? string.Empty) ?? Enumerable.Empty<string>();
        return !IsCompilation(release) && !Foreign(release, performers) && !secondary.Any(NotOriginal.Contains);
    }

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
