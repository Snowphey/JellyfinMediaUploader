using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Construit des chemins de destination sûrs (pas de traversée de dossier, caractères invalides retirés).
/// </summary>
public static class PathBuilder
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Nettoie un segment de chemin (nom de dossier ou de fichier).
    /// </summary>
    /// <param name="value">Valeur brute.</param>
    /// <param name="fallback">Valeur de repli si le résultat est vide.</param>
    /// <returns>Segment sûr.</returns>
    public static string SanitizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        var cleaned = MultiSpace.Replace(sb.ToString(), " ").Trim().Trim('.');
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > 150 ? cleaned[..150].TrimEnd() : cleaned;
    }

    /// <summary>
    /// Vérifie qu'un chemin complet reste bien sous la racine donnée.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="fullPath">Chemin à vérifier.</param>
    /// <returns>Vrai si le chemin est contenu dans la racine.</returns>
    public static bool IsInside(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(fullPath);
        return target.StartsWith(rootFull, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chemin d'un morceau : Artiste/Album/fichier.
    /// </summary>
    /// <param name="root">Racine musique.</param>
    /// <param name="artist">Artiste.</param>
    /// <param name="album">Album.</param>
    /// <param name="fileName">Nom du fichier.</param>
    /// <returns>Chemin complet.</returns>
    public static string Music(string root, string? artist, string? album, string fileName)
    {
        var file = SanitizeSegment(fileName, "file");
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        var hasAlbum = !string.IsNullOrWhiteSpace(album);

        if (!hasArtist && !hasAlbum)
        {
            return Path.Combine(root, file);
        }

        if (!hasAlbum)
        {
            return Path.Combine(root, SanitizeSegment(artist, "Unknown Artist"), file);
        }

        if (!hasArtist)
        {
            return Path.Combine(root, SanitizeSegment(album, "Unknown Album"), file);
        }

        return Path.Combine(
            root,
            SanitizeSegment(artist, "Unknown Artist"),
            SanitizeSegment(album, "Unknown Album"),
            file);
    }

    /// <summary>
    /// Chemin d'un fichier de film : Titre (Année)/fichier (nom d'origine conservé, pour que sous-titres et vidéo restent appariés).
    /// </summary>
    /// <param name="root">Racine films.</param>
    /// <param name="title">Titre.</param>
    /// <param name="year">Année (optionnelle).</param>
    /// <param name="fileName">Nom du fichier.</param>
    /// <returns>Chemin complet.</returns>
    public static string Movie(string root, string title, int? year, string fileName)
    {
        var safeTitle = SanitizeSegment(title, "Unknown Movie");
        var folder = year is > 1800 and < 2200 ? $"{safeTitle} ({year})" : safeTitle;
        return Path.Combine(root, folder, SanitizeSegment(fileName, "file"));
    }
}
