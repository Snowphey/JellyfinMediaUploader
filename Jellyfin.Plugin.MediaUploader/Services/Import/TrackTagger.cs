using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Écrit les tags d'un fichier téléchargé avec yt-dlp et récupère la pochette.
/// </summary>
public static class TrackTagger
{
    private const int MaxCoverBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Écrit titre, artiste, album, numéros de piste et de disque, année et pochette éventuelle.
    /// </summary>
    /// <param name="path">Fichier audio.</param>
    /// <param name="title">Titre.</param>
    /// <param name="artist">Artiste du morceau.</param>
    /// <param name="albumArtist">Artiste de l'album.</param>
    /// <param name="album">Album.</param>
    /// <param name="track">Numéro de piste.</param>
    /// <param name="disc">Numéro de disque.</param>
    /// <param name="year">Année.</param>
    /// <param name="cover">Pochette (JPEG ou PNG), ou null.</param>
    public static void Write(string path, string title, string artist, string albumArtist, string album, int? track, int? disc, int? year, byte[]? cover)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = title;
        tag.Performers = new[] { artist };
        tag.AlbumArtists = new[] { albumArtist };
        tag.Album = album;
        tag.Track = track is > 0 ? (uint)track.Value : 0;
        tag.Disc = disc is > 0 ? (uint)disc.Value : 0;
        tag.Year = year is > 0 ? (uint)year.Value : 0;
        if (cover is not null && MimeOf(cover) is { } mime)
        {
            tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = mime }
            };
        }

        file.Save();
    }

    /// <summary>
    /// Télécharge une pochette depuis un hébergeur d'images connu (YouTube, Spotify, Cover Art Archive). Ne lève jamais d'exception : une pochette manquante n'est pas bloquante.
    /// Trois tentatives (Cover Art Archive et archive.org répondent parfois 503 ou lentement), et la raison d'un échec est écrite dans le journal.
    /// </summary>
    /// <param name="url">Adresse (https, hébergeurs d'images connus uniquement).</param>
    /// <param name="ct">Annulation.</param>
    /// <param name="logger">Journal (facultatif).</param>
    /// <returns>Octets de l'image, ou null.</returns>
    public static async Task<byte[]?> FetchCoverAsync(string? url, CancellationToken ct, ILogger? logger = null)
    {
        if (!CoverHosts.Allowed(url, out var uri))
        {
            logger?.LogWarning("MediaUploader: pochette ignorée, hébergeur non autorisé : {Url}", url);
            return null;
        }

        string reason = "inconnue";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }

            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    reason = "HTTP " + (int)response.StatusCode;

                    // Introuvable : inutile de réessayer. Sinon (503, 429…) c'est probablement passager.
                    if ((int)response.StatusCode is 404 or 403 or 410)
                    {
                        break;
                    }

                    continue;
                }

                if (response.Content.Headers.ContentLength > MaxCoverBytes)
                {
                    reason = "image trop grosse";
                    break;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length is > 0 and <= MaxCoverBytes && MimeOf(bytes) is not null)
                {
                    return bytes;
                }

                reason = "format d'image non reconnu";
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                reason = ex.GetType().Name + " : " + ex.Message;
            }
        }

        logger?.LogWarning("MediaUploader: pochette non récupérée ({Reason}) : {Url}", reason, url);
        return null;
    }

    private static string? MimeOf(byte[] data)
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
}
