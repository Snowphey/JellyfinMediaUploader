using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Construit des chemins de destination sûrs (pas de traversée de dossier, caractères invalides retirés).
/// </summary>
public static class PathBuilder
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private const int MaxNameChars = 150;
    private const int MaxNameBytes = 240;

    /// <summary>
    /// Nettoie un segment de chemin (nom de dossier).
    /// </summary>
    /// <param name="value">Valeur brute.</param>
    /// <param name="fallback">Valeur de repli si le résultat est vide.</param>
    /// <returns>Segment sûr.</returns>
    public static string SanitizeSegment(string? value, string fallback)
    {
        var cleaned = Clean(value, MaxNameChars);
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>
    /// Nettoie un nom de fichier : le nom est raccourci si besoin, mais l'extension est toujours conservée
    /// (Jellyfin ignore un fichier sans extension reconnue) et le nom reste sous la limite des systèmes de fichiers (255 octets).
    /// </summary>
    /// <param name="name">Nom brut, extension comprise.</param>
    /// <param name="fallback">Nom de repli (sans extension) si le résultat est vide.</param>
    /// <returns>Nom de fichier sûr.</returns>
    public static string SanitizeFileName(string? name, string fallback)
    {
        var raw = name ?? string.Empty;
        var ext = Path.GetExtension(raw);
        if (ext.Length > 16 || ext.Contains(' '))
        {
            ext = string.Empty;
        }

        var stem = ext.Length > 0 ? raw[..^ext.Length] : raw;
        var cleanExt = Clean(ext.TrimStart('.'), 15);
        var suffix = cleanExt.Length > 0 ? "." + cleanExt : string.Empty;
        var cleaned = Clean(stem, Math.Max(20, MaxNameChars - suffix.Length));
        if (cleaned.Length == 0)
        {
            cleaned = fallback;
        }

        while (cleaned.Length > 1 && Encoding.UTF8.GetByteCount(cleaned + suffix) > MaxNameBytes)
        {
            var cut = char.IsLowSurrogate(cleaned[^1]) ? 2 : 1;
            cleaned = cleaned[..^Math.Min(cut, cleaned.Length - 1)].TrimEnd();
        }

        return cleaned + suffix;
    }

    private static string Clean(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
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
        return cleaned.Length > maxChars ? cleaned[..maxChars].TrimEnd() : cleaned;
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
    /// Dossier d'un morceau : Artiste/Album (ou la racine si on ne sait rien).
    /// </summary>
    /// <param name="root">Racine musique.</param>
    /// <param name="artist">Artiste.</param>
    /// <param name="album">Album.</param>
    /// <returns>Dossier.</returns>
    public static string MusicDir(string root, string? artist, string? album)
    {
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        var hasAlbum = !string.IsNullOrWhiteSpace(album);

        if (!hasArtist && !hasAlbum)
        {
            return root;
        }

        if (!hasAlbum)
        {
            return Path.Combine(root, SanitizeSegment(artist, "Unknown Artist"));
        }

        if (!hasArtist)
        {
            return Path.Combine(root, SanitizeSegment(album, "Unknown Album"));
        }

        return Path.Combine(root, SanitizeSegment(artist, "Unknown Artist"), SanitizeSegment(album, "Unknown Album"));
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
        return Path.Combine(MusicDir(root, artist, album), SanitizeFileName(fileName, "file"));
    }

    /// <summary>
    /// Nom de dossier « Titre (Année) » (ou « Titre » sans année plausible).
    /// </summary>
    /// <param name="title">Titre.</param>
    /// <param name="year">Année.</param>
    /// <param name="fallback">Valeur si le titre est vide.</param>
    /// <returns>Nom de dossier.</returns>
    public static string TitleFolder(string? title, int? year, string fallback)
    {
        var safeTitle = SanitizeSegment(title, fallback);
        return year is > 1800 and < 2200 ? $"{safeTitle} ({year})" : safeTitle;
    }

    /// <summary>
    /// Dossier d'un film : Titre (Année).
    /// </summary>
    /// <param name="root">Racine films.</param>
    /// <param name="title">Titre.</param>
    /// <param name="year">Année (optionnelle).</param>
    /// <returns>Dossier.</returns>
    public static string MovieDir(string root, string title, int? year)
    {
        return Path.Combine(root, TitleFolder(title, year, "Unknown Movie"));
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
        return Path.Combine(MovieDir(root, title, year), SanitizeFileName(fileName, "file"));
    }

    /// <summary>
    /// Dossier d'une saison : Série (Année)/Season 01 (« Season 00 » pour les épisodes spéciaux).
    /// </summary>
    /// <param name="root">Racine séries.</param>
    /// <param name="title">Titre de la série.</param>
    /// <param name="year">Année (optionnelle).</param>
    /// <param name="season">Saison (0 = spéciaux).</param>
    /// <returns>Dossier.</returns>
    public static string SeriesDir(string root, string title, int? year, int? season)
    {
        var show = Path.Combine(root, TitleFolder(title, year, "Unknown Series"));
        return season is null ? show : Path.Combine(show, $"Season {season.Value:00}");
    }

    /// <summary>
    /// Chemin d'un épisode : Série (Année)/Season 01/fichier (nom d'origine conservé).
    /// </summary>
    /// <param name="root">Racine séries.</param>
    /// <param name="title">Titre de la série.</param>
    /// <param name="year">Année (optionnelle).</param>
    /// <param name="season">Saison.</param>
    /// <param name="fileName">Nom du fichier.</param>
    /// <returns>Chemin complet.</returns>
    public static string Series(string root, string title, int? year, int? season, string fileName)
    {
        return Path.Combine(SeriesDir(root, title, year, season), SanitizeFileName(fileName, "file"));
    }
}
