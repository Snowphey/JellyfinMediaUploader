using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>Une mention d'artistes découpée.</summary>
/// <param name="Artists">Artistes, dans l'ordre (le premier est l'artiste principal).</param>
/// <param name="Source">Origine du découpage : "single" (un seul artiste), "name" (nom contenant une virgule, gardé entier), "rule" (règles locales), "musicbrainz" ou "user".</param>
public sealed record CreditSplit(IReadOnlyList<string> Artists, string Source)
{
    /// <summary>Gets l'artiste principal.</summary>
    public string Lead => Artists[0];
}

/// <summary>
/// Découpe une mention d'artistes (« K/DA, Madison Beer, i-dle », « Freeze Corleone feat. ASHE 22 ») en artistes distincts, sans réseau.
/// Les noms qui contiennent eux-mêmes une virgule (« Tyler, The Creator », « Earth, Wind &amp; Fire ») sont protégés : liste intégrée
/// et noms confirmés par l'utilisateur (MusicBrainz, s'il est consulté, connaît les autres).
/// « &amp; », « x », « et », « and », « with » ne séparent qu'avec une preuve : les deux noms sont déjà des artistes de la bibliothèque
/// (« Kalash Criminel &amp; Damso »), ou « &amp; » termine une liste à virgules (« Gradur, Ninho &amp; Damso ») ; sinon le nom reste entier (« Bigflo &amp; Oli »).
/// </summary>
public static class ArtistCredits
{
    /// <summary>Préfixe des clés d'une mention connue en entier (« Bigflo &amp; Oli » : un seul artiste).</summary>
    public const string StandalonePrefix = "=";

    /// <summary>Artiste des compilations et des bandes originales sans artiste unique.</summary>
    public const string VariousArtists = "Various Artists";

    // Noms connus contenant une virgule.
    private static readonly string[] BuiltInNames =
    {
        "Tyler, The Creator", "Earth, Wind & Fire", "Earth, Wind and Fire", "Crosby, Stills, Nash & Young", "Crosby, Stills & Nash",
        "Emerson, Lake & Palmer", "Peter, Paul and Mary", "Blood, Sweat & Tears", "Faith, Hope & Charity", "Up, Bustle and Out"
    };

    private static readonly Regex Separators = new(@"\s*[;,]\s*|\s+(?:feat\.?|ft\.?|featuring)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Joiners = new(@"\s+(?:&|x|et|and|with)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Ampersand = new(@"\s+&\s+", RegexOptions.Compiled);
    private static readonly Regex Cast = new(@"(^|[\s-])cast$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Vrai si la mention cite peut-être plusieurs artistes (virgule, point-virgule, « feat. »).
    /// </summary>
    /// <param name="credit">Mention.</param>
    /// <returns>Résultat.</returns>
    public static bool IsMultiple(string? credit) => !string.IsNullOrWhiteSpace(credit) && Separators.IsMatch(credit);

    /// <summary>
    /// Vrai si la mention contient un mot de liaison (« A &amp; B », « A x B », « A et B ») : duo ou deux artistes, à faire trancher par MusicBrainz.
    /// </summary>
    /// <param name="credit">Mention.</param>
    /// <returns>Résultat.</returns>
    public static bool HasJoiner(string? credit) => !string.IsNullOrWhiteSpace(credit) && Joiners.IsMatch(credit);

    /// <summary>
    /// Vrai si la mention est ambiguë : plusieurs noms, ou un « &amp; », « x », « et »… qui peut lier un duo ou deux artistes.
    /// </summary>
    /// <param name="credit">Mention.</param>
    /// <returns>Résultat.</returns>
    public static bool IsAmbiguous(string? credit) => IsMultiple(credit) || (!string.IsNullOrWhiteSpace(credit) && Joiners.IsMatch(credit));

    /// <summary>
    /// Tous les noms possibles d'une mention, en coupant partout (« A, B &amp; C » : A, B, C) : pour interroger MusicBrainz, jamais pour écrire des tags.
    /// </summary>
    /// <param name="credit">Mention.</param>
    /// <returns>Noms.</returns>
    public static IReadOnlyList<string> Candidates(string credit) =>
        Separators.Split(credit).SelectMany(t => Joiners.Split(t)).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>
    /// Clés des artistes cités par des mentions (virgules, « ; », « feat. » ; les mots de liaison ne sont pas coupés) : les artistes que la bibliothèque connaît.
    /// </summary>
    /// <param name="credits">Mentions (tags, noms de dossiers).</param>
    /// <returns>Clés.</returns>
    public static HashSet<string> KnownKeys(IEnumerable<string?> credits)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credit in credits.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()))
        {
            keys.UnionWith(Separators.Split(credit).Select(t => Key(t.Trim())).Where(k => k.Length > 0));

            // Nom cité seul (dossier, morceau d'un seul artiste) : « Bigflo & Oli » est alors un artiste à part entière.
            if (!Separators.IsMatch(credit))
            {
                keys.Add(StandalonePrefix + Key(credit));
            }
        }

        return keys;
    }

    /// <summary>
    /// Vrai si la mention contient des lettres autres que latines (japonais, coréen, cyrillique…).
    /// </summary>
    /// <param name="text">Texte.</param>
    /// <returns>Résultat.</returns>
    public static bool HasNonLatin(string? text) => !string.IsNullOrEmpty(text) && text.Any(c => char.IsLetter(c) && c > 'ɏ' && !(c >= 'Ḁ' && c <= 'ỿ'));

    /// <summary>
    /// Vrai si un artiste désigne la distribution d'un film ou d'une comédie musicale (« Encanto - Cast », « Original Broadway Cast »).
    /// </summary>
    /// <param name="artist">Artiste.</param>
    /// <returns>Résultat.</returns>
    public static bool IsCast(string artist) => Cast.IsMatch(artist.Trim());

    /// <summary>
    /// Découpe une mention.
    /// </summary>
    /// <param name="credit">Mention lue dans les tags.</param>
    /// <param name="knownNames">Noms à garder entiers (confirmés par l'utilisateur), comparés sans casse ni ponctuation ; null : liste intégrée seulement.</param>
    /// <param name="libraryArtists">Clés des artistes déjà présents dans la bibliothèque (<see cref="KnownKeys"/>) : « A &amp; B » n'est coupé que si A et B y sont.</param>
    /// <returns>Artistes.</returns>
    public static CreditSplit Split(string credit, ICollection<string>? knownNames = null, ISet<string>? libraryArtists = null)
    {
        var text = credit.Trim();
        if (!IsAmbiguous(text))
        {
            return new CreditSplit(new[] { text }, "single");
        }

        var known = new HashSet<string>(BuiltInNames.Select(Key), StringComparer.Ordinal);
        if (knownNames is not null)
        {
            known.UnionWith(knownNames.Select(Key));
        }

        if (IsName(text, known))
        {
            return new CreditSplit(new[] { text }, "name");
        }

        // Découpage, puis recollage des morceaux consécutifs qui forment un nom protégé (« A, Tyler, The Creator » : « A » + « Tyler, The Creator »).
        var tokens = Separators.Split(text).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var artists = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var taken = 1;
            for (var j = tokens.Count; j > i + 1; j--)
            {
                var joined = string.Join(", ", tokens.Skip(i).Take(j - i));
                if (IsName(joined, known))
                {
                    taken = j - i;
                    break;
                }
            }

            var token = string.Join(", ", tokens.Skip(i).Take(taken));
            i += taken - 1;

            // Mots de liaison : coupés seulement avec une preuve (voir la classe) ; un nom protégé reste entier.
            if (taken == 1 && !IsName(token, known) && Joiners.IsMatch(token))
            {
                var parts = Joiners.Split(token).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                var allKnown = libraryArtists is not null && parts.Count > 1 && parts.All(p => libraryArtists.Contains(Key(p)));
                // « A, B & C » : le « & » final sépare, sauf si la liste est faite de duos (« Mcfly & Carlito, Bigflo & Oli ») ou si la bibliothèque connaît déjà « B & C » comme un artiste cité seul.
                var listEnd = tokens.Count > 1 && i == tokens.Count - 1 && Ampersand.IsMatch(token) && !Joiners.IsMatch(Ampersand.Replace(token, " ", 1))
                    && !tokens.Take(tokens.Count - 1).Any(t => Joiners.IsMatch(t)) && libraryArtists?.Contains(StandalonePrefix + Key(token)) != true;
                if (allKnown)
                {
                    artists.AddRange(parts);
                    continue;
                }

                if (listEnd)
                {
                    artists.AddRange(Ampersand.Split(token).Select(t => t.Trim()).Where(t => t.Length > 0));
                    continue;
                }
            }

            artists.Add(token);
        }

        var distinct = artists.GroupBy(Key, StringComparer.Ordinal).Select(g => g.First()).Where(a => a.Length > 0).ToList();
        return distinct.Count <= 1 ? new CreditSplit(new[] { distinct.FirstOrDefault() ?? text }, "single") : new CreditSplit(distinct, "rule");
    }

    /// <summary>
    /// Artiste du dossier (et « artiste de l'album ») d'après les artistes d'un morceau : l'artiste principal,
    /// ou « Various Artists » pour une distribution de film (aucun artiste unique).
    /// </summary>
    /// <param name="artists">Artistes du morceau.</param>
    /// <returns>Artiste.</returns>
    public static string FolderArtist(IReadOnlyList<string> artists) => artists.Count > 1 && artists.Any(IsCast) ? VariousArtists : artists[0];

    /// <summary>
    /// Clé de comparaison d'un nom (casse, accents, ponctuation ignorés).
    /// </summary>
    /// <param name="name">Nom.</param>
    /// <returns>Clé.</returns>
    public static string Key(string name) => NameTools.TitleKey(name);

    // Nom à garder entier : liste intégrée ou nom confirmé. Aucune règle de forme : « Damso, Hamza & SCH » ressemble à « Earth, Wind & Fire »
    // et « Kendrick Lamar, The Weeknd » à « Tyler, The Creator » ; les autres noms à virgule viennent de MusicBrainz ou de l'utilisateur.
    private static bool IsName(string text, HashSet<string> known) => known.Contains(Key(text));
}
