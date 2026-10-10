using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Nettoyage des titres et noms d'artistes tels qu'ils arrivent de YouTube, et nom de fichier d'un morceau.
/// </summary>
public static class TrackNaming
{
    private static readonly Regex Noise = new(
        @"\s*[\(\[\{][^\)\]\}]*\b(official|officiel(le)?|video|vidéo|audio|lyrics?|lyric video|paroles|visuali[sz]er|music video|clip|hd|hq|4k|mv|full album)\b[^\)\]\}]*[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Dash = new(@"^(?<a>.{1,120}?)\s+[-–—]\s+(?<t>.+)$", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Retire les mentions parasites entre parenthèses (« (Official Video) », « [Lyrics] »…).
    /// </summary>
    /// <param name="title">Titre brut.</param>
    /// <returns>Titre nettoyé.</returns>
    public static string CleanTitle(string? title)
    {
        var cleaned = Noise.Replace(title ?? string.Empty, string.Empty);
        return Spaces.Replace(cleaned, " ").Trim();
    }

    /// <summary>
    /// Nom d'artiste d'une chaîne YouTube : « Artiste - Topic » et « ArtisteVEVO » deviennent « Artiste ».
    /// </summary>
    /// <param name="channel">Nom de la chaîne.</param>
    /// <returns>Artiste.</returns>
    public static string ArtistFromChannel(string? channel)
    {
        var name = (channel ?? string.Empty).Trim();
        if (name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^8].Trim();
        }

        if (name.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
        {
            return name[..^4].Trim();
        }

        return name;
    }

    /// <summary>
    /// Sépare « Artiste - Titre » quand le titre de la vidéo suit cette forme ; sinon l'artiste est la chaîne.
    /// </summary>
    /// <param name="videoTitle">Titre de la vidéo.</param>
    /// <param name="channel">Chaîne.</param>
    /// <returns>Artiste et titre.</returns>
    public static (string Artist, string Title) FromVideo(string? videoTitle, string? channel)
    {
        var title = CleanTitle(videoTitle);
        var artist = ArtistFromChannel(channel);
        var isTopic = (channel ?? string.Empty).EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
        if (!isTopic && Dash.Match(title) is { Success: true } m)
        {
            return (m.Groups["a"].Value.Trim(), m.Groups["t"].Value.Trim());
        }

        return (artist, title);
    }

    /// <summary>
    /// Nom de fichier d'un morceau : « 05 - Titre.m4a » (« 2-05 - Titre.m4a » à partir du deuxième disque).
    /// </summary>
    /// <param name="disc">Disque.</param>
    /// <param name="track">Piste.</param>
    /// <param name="title">Titre.</param>
    /// <param name="extension">Extension avec le point.</param>
    /// <returns>Nom de fichier sûr.</returns>
    public static string FileName(int? disc, int? track, string title, string extension)
    {
        var prefix = track is > 0 ? (disc is > 1 ? $"{disc}-{track:00}" : $"{track:00}") + " - " : string.Empty;
        return PathBuilder.SanitizeSegment(prefix + title, "Track") + extension;
    }
}
