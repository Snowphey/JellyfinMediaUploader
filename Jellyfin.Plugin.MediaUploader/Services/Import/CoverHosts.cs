namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Hébergeurs d'images dont le serveur accepte de télécharger une pochette (le serveur ne suit pas une adresse quelconque).
/// </summary>
public static class CoverHosts
{
    private static readonly string[] Suffixes = { ".ytimg.com", ".googleusercontent.com", ".ggpht.com", ".scdn.co", ".archive.org" };

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
