namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Hébergeurs d'images dont le serveur accepte de télécharger une pochette (le serveur ne suit pas une adresse quelconque).
/// </summary>
public static class CoverHosts
{
    private static readonly string[] Suffixes = { ".ytimg.com", ".googleusercontent.com", ".ggpht.com", ".scdn.co", ".archive.org", ".mzstatic.com" };

    /// <summary>
    /// Type d'une image d'après ses premiers octets.
    /// </summary>
    /// <param name="data">Contenu.</param>
    /// <returns>"image/jpeg" ou "image/png", ou null si ce n'est ni l'un ni l'autre.</returns>
    public static string? ImageMime(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return "image/jpeg";
        }

        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        return null;
    }

    /// <summary>
    /// Indique si une adresse de pochette est acceptée (https, hébergeurs d'images de YouTube, Spotify et Cover Art Archive).
    /// </summary>
    /// <param name="url">Adresse.</param>
    /// <param name="uri">Adresse analysée.</param>
    /// <returns>Vrai si acceptée.</returns>
    public static bool Allowed(string? url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https")
        {
            return false;
        }

        var host = u.Host.ToLowerInvariant();
        var ok = host == "coverartarchive.org" || Suffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
        uri = ok ? u : null;
        return ok;
    }
}
