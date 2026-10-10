using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Origine d'un lien d'import.</summary>
public enum ImportSource
{
    /// <summary>YouTube ou YouTube Music (yt-dlp).</summary>
    YouTube,

    /// <summary>Spotify (lecture des métadonnées, audio via yt-dlp).</summary>
    Spotify
}

/// <summary>
/// Lien d'import reconnu et ramené à une forme canonique.
/// </summary>
/// <param name="Source">Origine.</param>
/// <param name="Kind">"playlist", "album", "track" ou "artist".</param>
/// <param name="Url">Adresse canonique, reconstruite à partir d'identifiants validés.</param>
/// <param name="Id">Identifiant de la ressource.</param>
public sealed record ParsedLink(ImportSource Source, string Kind, string Url, string Id);

/// <summary>
/// Reconnaissance des liens acceptés. Seuls YouTube, YouTube Music et Spotify sont admis, et l'adresse transmise aux outils
/// est reconstruite à partir d'identifiants validés : le serveur ne devient pas un téléchargeur universel ni un relais vers le réseau local.
/// </summary>
public static class ImportUrl
{
    private static readonly Regex SpotifyId = new("^[A-Za-z0-9]{22}$", RegexOptions.Compiled);
    private static readonly Regex YouTubeId = new("^[A-Za-z0-9_-]{6,80}$", RegexOptions.Compiled);

    /// <summary>
    /// Analyse un lien saisi par l'utilisateur.
    /// </summary>
    /// <param name="input">Lien (ou URI « spotify:playlist:… »).</param>
    /// <param name="error">Message en cas de refus.</param>
    /// <returns>Lien reconnu, ou null.</returns>
    public static ParsedLink? Parse(string? input, out string? error)
    {
        error = null;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 600)
        {
            error = "Collez un lien Spotify, YouTube ou YouTube Music.";
            return null;
        }

        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(':');
            if (parts.Length == 3 && SpotifyLink(parts[1], parts[2]) is { } fromUri)
            {
                return fromUri;
            }

            error = "URI Spotify non reconnue.";
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "Ce n'est pas un lien web valide.";
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        else if (host.StartsWith("m.", StringComparison.Ordinal))
        {
            host = host[2..];
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);

        if (host == "open.spotify.com")
        {
            var skip = 0;
            while (skip < segments.Length && (segments[skip].StartsWith("intl-", StringComparison.OrdinalIgnoreCase) || segments[skip].Equals("embed", StringComparison.OrdinalIgnoreCase)))
            {
                skip++;
            }

            if (segments.Length >= skip + 2 && SpotifyLink(segments[skip], segments[skip + 1]) is { } link)
            {
                return link;
            }

            error = "Lien Spotify non reconnu : utilisez le lien d'une playlist, d'un album ou d'un titre.";
            return null;
        }

        if (host is "youtube.com" or "music.youtube.com" or "youtu.be")
        {
            var music = host == "music.youtube.com";
            var web = music ? "https://music.youtube.com" : "https://www.youtube.com";
            query.TryGetValue("list", out var list);
            query.TryGetValue("v", out var video);

            if (host == "youtu.be" && segments.Length >= 1)
            {
                video = segments[0];
            }

            if (!string.IsNullOrEmpty(list))
            {
                if (!YouTubeId.IsMatch(list))
                {
                    error = "Identifiant de playlist invalide.";
                    return null;
                }

                if (list.StartsWith("RD", StringComparison.Ordinal))
                {
                    error = "Les « mix » YouTube sont infinis : choisissez une vraie playlist ou un album.";
                    return null;
                }

                if (list is "LL" or "WL" || list.StartsWith("LM", StringComparison.Ordinal))
                {
                    error = "Les listes personnelles (titres aimés, à regarder plus tard) demandent un compte : non prises en charge.";
                    return null;
                }

                var kind = list.StartsWith("OLAK5uy_", StringComparison.Ordinal) ? "album" : "playlist";
                return new ParsedLink(ImportSource.YouTube, kind, $"{web}/playlist?list={list}", list);
            }

            if (music && segments.Length == 2 && segments[0] == "browse" && segments[1].StartsWith("MPRE", StringComparison.Ordinal) && YouTubeId.IsMatch(segments[1]))
            {
                return new ParsedLink(ImportSource.YouTube, "album", $"{web}/browse/{segments[1]}", segments[1]);
            }

            if (!string.IsNullOrEmpty(video) && YouTubeId.IsMatch(video) && video.Length == 11)
            {
                return new ParsedLink(ImportSource.YouTube, "track", $"{web}/watch?v={video}", video);
            }

            error = "Lien YouTube non reconnu : utilisez une playlist, un album YouTube Music ou une vidéo.";
            return null;
        }

        error = "Seuls les liens Spotify, YouTube et YouTube Music sont acceptés.";
        return null;
    }

    private static ParsedLink? SpotifyLink(string type, string id)
    {
        type = type.ToLowerInvariant();
        if (type is not ("playlist" or "album" or "track" or "artist") || !SpotifyId.IsMatch(id))
        {
            return null;
        }

        return new ParsedLink(ImportSource.Spotify, type, $"https://open.spotify.com/{type}/{id}", id);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }

        return result;
    }
}
