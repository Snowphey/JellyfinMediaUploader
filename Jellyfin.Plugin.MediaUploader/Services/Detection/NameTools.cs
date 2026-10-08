using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Petits outils partagés de lecture de noms : suffixes de sous-titres, normalisation des titres, année entre parenthèses.
/// </summary>
public static class NameTools
{
    /// <summary>Extensions de sous-titres externes que Jellyfin sait apparier à une vidéo.</summary>
    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup"
    };

    // « Titre (2019).fr.srt » : suffixes de langue et d'indicateurs (.fr, .en.forced, .pt-BR, .sdh...).
    private static readonly Regex LangSuffix = new(
        @"(\.(?:[a-z]{2,3}(?:-[a-z]{2,4})?|forced|sdh|default|cc|hi))+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingTags = new(@"^(?:\[[^\]]*\]\s*)+", RegexOptions.Compiled);

    private static readonly Regex TrailingYear = new(
        @"^(?<t>.+?)(?:\s*[(\[]\s*(?<y>(?:19|20)\d{2})\s*[)\]]|\s+(?<y2>(?:19|20)\d{2}))$",
        RegexOptions.Compiled);

    private static readonly Regex SeasonFolder = new(
        @"^(?:season|saison|series|staffel|temporada|s)[ ._\-]*\d{1,2}$|^(?:specials?|extras?|sp)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Retire les répétitions d'un champ artiste : « A, A, A » devient « A » (« A, B, A » devient « A, B »).
    /// Seuls des éléments identiques (sans tenir compte de la casse) sont fusionnés : « Tyler, The Creator » reste intact.
    /// </summary>
    /// <param name="artist">Valeur lue dans les tags.</param>
    /// <returns>Valeur sans répétition, ou null si vide.</returns>
    public static string? DedupeArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var parts = artist.Split(new[] { ", ", "; " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = parts.Where(p => seen.Add(p)).ToList();
        return kept.Count == 0 ? artist.Trim() : string.Join(", ", kept);
    }

    /// <summary>
    /// Vrai si l'extension est celle d'un sous-titre.
    /// </summary>
    /// <param name="ext">Extension avec point.</param>
    /// <returns>Résultat.</returns>
    public static bool IsSubtitle(string ext) => SubtitleExtensions.Contains(ext);

    /// <summary>
    /// Remplace les séparateurs de chemin Windows et retire les « / » de bord.
    /// </summary>
    /// <param name="path">Chemin côté client.</param>
    /// <returns>Chemin à séparateurs « / ».</returns>
    public static string NormalizePath(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Dernier segment d'un chemin côté client.
    /// </summary>
    /// <param name="clientPath">Chemin.</param>
    /// <returns>Nom du fichier.</returns>
    public static string FileNameOf(string clientPath)
    {
        var p = NormalizePath(clientPath);
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>
    /// Nom sans extension, sans suffixe de langue pour un sous-titre (le suffixe n'est retiré que s'il reste un nom).
    /// </summary>
    /// <param name="fileName">Nom du fichier avec extension.</param>
    /// <returns>Radical.</returns>
    public static string StemOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (IsSubtitle(ext))
        {
            var stripped = LangSuffix.Replace(stem, string.Empty);
            if (stripped.Length > 0)
            {
                stem = stripped;
            }
        }

        return stem;
    }

    /// <summary>
    /// Suffixe de langue d'un sous-titre (« .fr », « .en.forced »), ou chaîne vide.
    /// </summary>
    /// <param name="fileName">Nom du fichier avec extension.</param>
    /// <returns>Suffixe.</returns>
    public static string LangSuffixOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = LangSuffix.Match(stem);
        return m.Success && m.Index > 0 ? m.Value : string.Empty;
    }

    /// <summary>
    /// Nettoie un titre capturé : étiquettes de groupe « [Groupe] » en tête, points et tirets bas du style torrent, espaces multiples.
    /// </summary>
    /// <param name="raw">Texte brut.</param>
    /// <returns>Titre lisible (éventuellement vide).</returns>
    public static string NormalizeTitle(string? raw)
    {
        var s = LeadingTags.Replace(raw ?? string.Empty, string.Empty);
        if (!s.Contains(' '))
        {
            s = s.Replace('.', ' ').Replace('_', ' ');
        }

        return MultiSpace.Replace(s, " ").Trim(' ', '-', '(', '[', '.', '_');
    }

    /// <summary>
    /// Sépare une année placée à la fin d'un titre (« Série (2019) », « Série [2019] », « Série 2019 »).
    /// </summary>
    /// <param name="title">Titre normalisé.</param>
    /// <param name="maxYear">Année maximale plausible.</param>
    /// <returns>Titre sans l'année, et l'année si trouvée.</returns>
    public static (string Title, int? Year) SplitYear(string title, int maxYear)
    {
        var m = TrailingYear.Match(title);
        if (!m.Success)
        {
            return (title, null);
        }

        var y = int.Parse(m.Groups["y"].Success ? m.Groups["y"].Value : m.Groups["y2"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (y > maxYear)
        {
            return (title, null);
        }

        var t = m.Groups["t"].Value.Trim(' ', '-', '(', '[', '.', '_');
        return t.Length == 0 ? (title, null) : (t, y);
    }

    /// <summary>
    /// Vrai si un nom de dossier désigne une saison (« Season 2 », « S02 », « Specials »).
    /// </summary>
    /// <param name="folder">Nom de dossier.</param>
    /// <returns>Résultat.</returns>
    public static bool IsSeasonFolder(string folder) => SeasonFolder.IsMatch(folder.Trim());

    /// <summary>
    /// Clé de comparaison de titres : minuscules, lettres et chiffres seulement, sans accents.
    /// </summary>
    /// <param name="title">Titre.</param>
    /// <returns>Clé.</returns>
    public static string TitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var d = title.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
