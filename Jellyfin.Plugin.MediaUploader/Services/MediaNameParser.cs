using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Déduit titre et année d'un film à partir du nom de fichier
/// (« Titre (2019).mkv », « Titre.2019.1080p.BluRay.x264-GRP.mkv », « Titre [2019] »...).
/// </summary>
public static class MediaNameParser
{
    private static readonly Regex YearToken = new(
        @"(?<=^|[\s.\-_(\[])(19\d{2}|20\d{2})(?=$|[\s.\-_)\]])",
        RegexOptions.Compiled);

    // Suffixe de langue/flag d'un sous-titre : « Titre (2019).fr.srt » -> « Titre (2019) ».
    private static readonly Regex SubtitleSuffix = new(
        @"(\.(?:[a-z]{2,3}|forced|sdh|default|cc))+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup"
    };

    /// <summary>
    /// Tente d'extraire (titre, année) d'un nom de fichier. Retourne null si l'année est introuvable
    /// ou si aucun titre ne la précède : dans ce cas le fichier reste à la racine.
    /// </summary>
    /// <param name="fileName">Nom du fichier avec extension.</param>
    /// <returns>Titre et année, ou null.</returns>
    public static (string Title, int Year)? ParseMovie(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (SubtitleExtensions.Contains(ext))
        {
            stem = SubtitleSuffix.Replace(stem, string.Empty);
        }

        var matches = YearToken.Matches(stem);
        var maxYear = DateTime.UtcNow.Year + 2;

        // On prend la dernière année plausible qui a un titre devant elle
        // (« 2001.A.Space.Odyssey.1968.1080p » -> 1968 ; « Wonder Woman 1984 (2020) » -> 2020).
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            var year = int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (year > maxYear || m.Index == 0)
            {
                continue;
            }

            var raw = stem[..m.Index];

            // Style torrent : points ou underscores comme séparateurs (pas d'espaces).
            if (!raw.Contains(' '))
            {
                raw = raw.Replace('.', ' ').Replace('_', ' ');
            }

            var title = MultiSpace.Replace(raw, " ").Trim(' ', '-', '(', '[', '.');
            if (title.Length == 0)
            {
                continue;
            }

            return (title, year);
        }

        return null;
    }
}
