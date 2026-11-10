using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Clé de comparaison d'un morceau : artistes et titre réduits à l'essentiel.
/// </summary>
public sealed class SongKey
{
    /// <summary>Gets les artistes, chacun réduit à ses lettres et chiffres (« daftpunk »).</summary>
    public required IReadOnlySet<string> Artists { get; init; }

    /// <summary>Gets l'ensemble des artistes réduit d'un bloc.</summary>
    public required string ArtistFull { get; init; }

    /// <summary>Gets le titre réduit (sans mentions « Official Video », « Remastered », invités…).</summary>
    public required string Title { get; init; }

    /// <summary>Gets les mentions qui font une autre version (« live », « remix », « acoustic »…), triées.</summary>
    public required string Markers { get; init; }

    /// <summary>Gets les mots du titre réduit, dans l'ordre (pour reconnaître un titre tronqué).</summary>
    public IReadOnlyList<string> TitleWords { get; init; } = Array.Empty<string>();

    /// <summary>Gets les mots des parenthèses descriptives (« (Intro) », « (Start a Band) ») : deux sous-titres différents font deux morceaux.</summary>
    public IReadOnlySet<string> Extra { get; init; } = new HashSet<string>();
}

/// <summary>
/// Détection souple des doublons de musique : même artiste (au moins un en commun) et même titre, en ignorant la casse, les accents,
/// la ponctuation, les mentions ajoutées par YouTube ou les rééditions (« (Official Video) », « - Remastered 2011 », « [HD] »), les invités
/// (« feat. X ») et l'artiste répété en tête du titre (« Artiste - Titre »). Une version live, remix, acoustique, instrumentale… reste distincte.
/// </summary>
public static class DuplicateMatcher
{
    private static readonly Regex Brackets = new(@"[\(\[\{（【]([^\)\]\}）】]*)[\)\]\}）】]", RegexOptions.Compiled);
    private static readonly Regex Featuring = new(@"\s+(feat\.?|ft\.?|featuring|avec)\s.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Tiret séparateur : espacé des deux côtés, ou au moins d'un (« Titre -Archetype Mix Ver. ») ; « Spider-Man » n'est pas coupé.
    private static readonly Regex Dash = new(@"\s+[-‐‑–—|]\s*|\s*[-‐‑–—|]\s+", RegexOptions.Compiled);
    private static readonly Regex Tildes = new(@"[～~〜]([^～~〜]+)[～~〜]", RegexOptions.Compiled);
    private static readonly Regex GuestBracket = new(@"^\s*(feat\.?|ft\.?|featuring|with|avec|prod\.?|produced)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SourceTail = new(@"^\s*(from|de|du|des|extrait|taken from|tiré|theme from|bande originale|ost|original soundtrack|original motion picture)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSplit = new(@"\s*(?:,|;|&|/|\+|\bx\b|\bvs\.?(?=\s)|\bfeat\.?|\bft\.?(?=\s)|\bfeaturing\b|\bwith\b|\band\b|\bet\b)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistNoise = new(@"(\s*-\s*topic|vevo|\s+official)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Numbered = new(@"\b(part|pt|vol|volume|chapter|chapitre|no|n°|op|act|acte)\.?\s*\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingThe = new(@"^(the|les?|la)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    // Mentions qui désignent un autre enregistrement : jamais un doublon de la version studio.
    private static readonly Dictionary<string, string> MarkerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["live"] = "live",
        ["concert"] = "live",
        ["remix"] = "remix",
        ["rmx"] = "remix",
        ["bootleg"] = "remix",
        ["rework"] = "remix",
        ["vip"] = "remix",
        ["acoustic"] = "acoustic",
        ["acoustique"] = "acoustic",
        ["unplugged"] = "acoustic",
        ["instrumental"] = "instrumental",
        ["karaoke"] = "instrumental",
        ["cover"] = "cover",
        ["demo"] = "demo",
        ["nightcore"] = "speed",
        ["sped"] = "speed",
        ["slowed"] = "speed",
        ["extended"] = "extended",
        ["orchestral"] = "orchestral",
        ["piano"] = "piano",
        ["reprise"] = "reprise",
        ["arrange"] = "arrange",
        ["arranged"] = "arrange",
        ["arrangement"] = "arrange"
    };

    // Mentions sans importance derrière un tiret (« Titre - Remastered 2011 ») ; ailleurs, toute parenthèse sans mention de version est ignorée.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "remaster", "remastered", "remasterise", "remasterisé", "official", "officiel", "video", "vidéo", "clip", "audio", "lyrics", "lyric", "paroles",
        "visualizer", "visualiser", "version", "edit", "radio", "single", "album", "mono", "stereo", "explicit", "clean", "bonus", "track", "deluxe",
        "hd", "hq", "4k", "mv", "m", "v", "from", "original", "mix", "soundtrack", "ost", "theme", "music", "musique", "anniversary", "edition", "remake", "topic", "ver"
    };

    /// <summary>
    /// Calcule la clé d'un morceau.
    /// </summary>
    /// <param name="artist">Artiste(s) des tags.</param>
    /// <param name="title">Titre des tags.</param>
    /// <returns>Clé, ou null sans artiste ou sans titre exploitable.</returns>
    public static SongKey? Key(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleanArtist = ArtistNoise.Replace(artist.Trim(), string.Empty);
        var artists = ArtistSplit.Split(cleanArtist).Select(ArtistKey).Where(a => a.Length >= 2).ToHashSet(StringComparer.Ordinal);
        var full = ArtistKey(cleanArtist);
        if (artists.Count == 0 && full.Length == 0)
        {
            return null;
        }

        var markers = new SortedSet<string>(StringComparer.Ordinal);
        var extra = new HashSet<string>(StringComparer.Ordinal);
        var t = Tildes.Replace(title.Trim(), " ($1) ");

        // Parenthèses et crochets : une mention de version est retenue, une numérotation (« Part 2 ») reste dans le titre ;
        // une mention descriptive (« (Intro) ») est mise à part : elle ne bloque que face à une autre mention différente.
        t = Brackets.Replace(t, m =>
        {
            var inner = m.Groups[1].Value;

            // Mention de la source (« (from the series Arcane) », « (Bande originale du film) ») ou des invités : sans importance.
            if (SourceTail.IsMatch(inner) || GuestBracket.IsMatch(inner))
            {
                return " ";
            }

            if (AddMarkers(inner, markers))
            {
                // « (rain, instrumental version) » : la mention de version est retenue, et ses autres mots aussi.
                AddExtra(inner, extra);
                return " ";
            }

            if (Numbered.IsMatch(inner))
            {
                return " " + inner + " ";
            }

            if (!IsNoise(inner))
            {
                AddExtra(inner, extra);
            }

            return " ";
        });

        // « Artiste - Titre - Remastered 2011 » : l'artiste en tête et les mentions en queue tombent.
        var parts = Dash.Split(t).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count > 1)
        {
            var head = ArtistKey(ArtistNoise.Replace(parts[0], string.Empty));
            if (head.Length > 0 && (head == full || artists.Contains(head) || ArtistSplit.Split(parts[0]).Select(ArtistKey).Any(artists.Contains)))
            {
                parts.RemoveAt(0);
            }

            for (var i = parts.Count - 1; i >= 1; i--)
            {
                // L'artiste en queue (« Gustave - Lorien Testard ») tombe comme en tête.
                var tail = ArtistKey(ArtistNoise.Replace(parts[i], string.Empty));
                if (tail.Length > 0 && (tail == full || artists.Contains(tail)))
                {
                    parts.RemoveAt(i);
                    continue;
                }

                if (SourceTail.IsMatch(parts[i]) || IsNoise(parts[i]))
                {
                    parts.RemoveAt(i);
                    continue;
                }

                if (AddMarkers(parts[i], markers))
                {
                    AddExtra(parts[i], extra);
                    parts.RemoveAt(i);
                }
            }

            t = string.Join(" ", parts);
        }

        t = Featuring.Replace(t, string.Empty);
        var key = NameTools.TitleKey(t);
        if (key.Length == 0)
        {
            return null;
        }

        var words = Words.Matches(t).Select(x => NameTools.TitleKey(x.Value)).Where(w => w.Length > 0).ToList();
        return new SongKey { Artists = artists, ArtistFull = full, Title = key, Markers = string.Join("+", markers), TitleWords = words, Extra = extra };
    }

    /// <summary>
    /// Vrai si deux morceaux sont probablement le même enregistrement.
    /// </summary>
    /// <param name="a">Premier.</param>
    /// <param name="b">Second.</param>
    /// <returns>Résultat.</returns>
    public static bool Same(SongKey a, SongKey b) => SameArtist(a, b) && SameTitle(a, b);

    /// <summary>
    /// Vrai si les deux morceaux ont au moins un artiste en commun.
    /// </summary>
    /// <param name="a">Premier.</param>
    /// <param name="b">Second.</param>
    /// <returns>Résultat.</returns>
    public static bool SameArtist(SongKey a, SongKey b)
    {
        if (a.ArtistFull.Length > 0 && a.ArtistFull == b.ArtistFull)
        {
            return true;
        }

        if (a.Artists.Overlaps(b.Artists))
        {
            return true;
        }

        // « Daft Punk » / « Daft Punk Official », « The Weeknd » / « Weeknd ».
        var (s, l) = a.ArtistFull.Length <= b.ArtistFull.Length ? (a.ArtistFull, b.ArtistFull) : (b.ArtistFull, a.ArtistFull);
        return s.Length >= 4 && l.StartsWith(s, StringComparison.Ordinal);
    }

    /// <summary>
    /// Vrai si les titres désignent le même morceau (même version).
    /// </summary>
    /// <param name="a">Premier.</param>
    /// <param name="b">Second.</param>
    /// <returns>Résultat.</returns>
    public static bool SameTitle(SongKey a, SongKey b)
    {
        if (a.Markers != b.Markers)
        {
            return false;
        }

        // Mentions descriptives : « (Intro) » / « (Start a Band) », « Lune » / « Lune (music box) », « The Land » / « The Land (Choir) » font
        // des morceaux distincts ; l'une peut seulement compléter l'autre (« (We Never Run) » / « (We Never Run) (Extended Credits) » non).
        if (a.Extra.Count != b.Extra.Count || !a.Extra.SetEquals(b.Extra))
        {
            if (a.Extra.Count == 0 || b.Extra.Count == 0 || (!a.Extra.IsSubsetOf(b.Extra) && !b.Extra.IsSubsetOf(a.Extra)))
            {
                return false;
            }
        }

        if (a.Title == b.Title)
        {
            return true;
        }

        var (s, l) = a.Title.Length <= b.Title.Length ? (a.Title, b.Title) : (b.Title, a.Title);

        // Un chiffre de différence (« Part 1 » / « Part 2 », « Symphony No. 5 » / « No. 9 ») : deux morceaux distincts.
        if (!new string(s.Where(char.IsDigit).ToArray()).Equals(new string(l.Where(char.IsDigit).ToArray()), StringComparison.Ordinal))
        {
            return false;
        }

        // Titre suivi de mentions sans importance (« Bohemian Rhapsody » / « Bohemian Rhapsody Remastered ») ;
        // un sous-titre (« Jujutsu Sorcerer » / « Jujutsu Sorcerer - Nobara Kugisaki ») fait un autre morceau.
        var (ws, wl) = a.TitleWords.Count <= b.TitleWords.Count ? (a.TitleWords, b.TitleWords) : (b.TitleWords, a.TitleWords);
        if (s.Length >= 8 && ws.Count > 0 && wl.Take(ws.Count).SequenceEqual(ws) && wl.Skip(ws.Count).All(w => NoiseWords.Contains(w) || w.All(char.IsDigit)))
        {
            return true;
        }

        // Faute de frappe (« Prise De Consciense » / « Prise de conscience ») : un seul mot diffère, de peu, et commence par la même lettre.
        // Une ressemblance globale ne suffit pas : « L'Amour d'un Père » / « L'Amour d'une Mère », « 脱出 » / « 逃走 » sont des morceaux distincts.
        // (Apostrophes, ponctuation et espaces ne comptent déjà pas : « Dont Stop Me Now » et « Don't Stop Me Now » ont la même clé.)
        if (a.TitleWords.Count != b.TitleWords.Count)
        {
            return false;
        }

        var differing = a.TitleWords.Zip(b.TitleWords).Where(p => p.First != p.Second).ToList();
        return differing.Count == 1
            && differing[0].First.Length >= 5 && differing[0].Second.Length >= 5
            && differing[0].First[0] == differing[0].Second[0]
            && Similarity(differing[0].First, differing[0].Second) >= 0.75;
    }

    /// <summary>
    /// Ressemblance de deux chaînes (1 = identiques), d'après la distance de Levenshtein.
    /// </summary>
    /// <param name="a">Première.</param>
    /// <param name="b">Seconde.</param>
    /// <returns>Valeur entre 0 et 1.</returns>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return 1.0 - ((double)prev[b.Length] / Math.Max(a.Length, b.Length));
    }

    private static bool AddMarkers(string text, SortedSet<string> markers)
    {
        var found = false;
        var words = Words.Matches(text).Select(m => m.Value).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];

            // « Original Mix », « Radio Mix » : la version normale ; « Club Mix », « XYZ Mix » : un remix.
            if (w.Equals("mix", StringComparison.OrdinalIgnoreCase))
            {
                var before = i > 0 ? words[i - 1].ToLowerInvariant() : string.Empty;
                if (before is not ("original" or "radio" or "album" or "single" or "stereo" or "mono" or "2011" or "new"))
                {
                    markers.Add("remix");
                    found = true;
                }

                continue;
            }

            if (MarkerWords.TryGetValue(w, out var marker))
            {
                // « Cover Art » n'est pas une reprise.
                if (marker == "cover" && i + 1 < words.Count && words[i + 1].Equals("art", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                markers.Add(marker);
                found = true;
            }
        }

        return found;
    }

    private static void AddExtra(string text, HashSet<string> extra)
    {
        foreach (var w in Words.Matches(text).Select(x => NameTools.TitleKey(x.Value)).Where(w => w.Length > 0 && !NoiseWords.Contains(w) && !MarkerWords.ContainsKey(w)))
        {
            extra.Add(w);
        }
    }

    private static bool IsNoise(string text)
    {
        var words = Words.Matches(text).Select(m => m.Value).ToList();
        return words.Count > 0 && words.All(w => NoiseWords.Contains(w) || w.All(char.IsDigit));
    }

    // « The Weeknd » et « Weeknd » : l'article en tête ne compte pas.
    private static string ArtistKey(string artist) => NameTools.TitleKey(LeadingThe.Replace(artist.Trim(), string.Empty));
}
