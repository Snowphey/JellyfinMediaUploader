using Jellyfin.Plugin.MediaUploader.Services.Import;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Image de pochette prête à être enregistrée.</summary>
/// <param name="Data">Octets.</param>
/// <param name="Mime">"image/jpeg" ou "image/png".</param>
/// <param name="Label">Origine, pour le journal.</param>
public sealed record CoverImageData(byte[] Data, string Mime, string Label);

/// <summary>
/// Obtient l'image d'une pochette choisie : proposition de Cover Art Archive ou d'iTunes, ou image envoyée par l'utilisateur.
/// Commun à la page « Bibliothèque » et à l'aperçu d'un lot.
/// </summary>
public static class CoverImage
{
    /// <summary>Taille maximale d'une pochette.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Télécharge ou décode l'image choisie.
    /// </summary>
    /// <param name="source">"musicbrainz" ou "itunes" (ignorée si une image est envoyée).</param>
    /// <param name="id">MBID du groupe de sorties (source "musicbrainz").</param>
    /// <param name="url">Adresse de l'image (source "itunes").</param>
    /// <param name="imageBase64">Image envoyée (base64, éventuellement en « data: »).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Image, ou un message d'erreur (et vrai si l'erreur vient de la source distante).</returns>
    public static async Task<(CoverImageData? Image, string? Error, bool Remote)> ResolveAsync(string? source, string? id, string? url, string? imageBase64, ILogger logger, CancellationToken ct)
    {
        byte[]? image;
        string label;
        if (!string.IsNullOrEmpty(imageBase64))
        {
            var text = imageBase64;
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            if (text.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
            {
                text = text[(comma + 1)..];
            }

            if (text.Length > 12_000_000)
            {
                return (null, "Image trop grosse (8 Mo au plus).", false);
            }

            try
            {
                image = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return (null, "Image illisible.", false);
            }

            label = "image envoyée";
        }
        else if (source == "musicbrainz" && Guid.TryParse(id, out var mbid))
        {
            image = await TrackTagger.FetchCoverAsync($"https://coverartarchive.org/release-group/{mbid}/front-1200", ct, logger).ConfigureAwait(false)
                ?? await TrackTagger.FetchCoverAsync($"https://coverartarchive.org/release-group/{mbid}/front-500", ct, logger).ConfigureAwait(false);
            label = "Cover Art Archive (MusicBrainz " + mbid + ")";
        }
        else if (source == "itunes" && CoverHosts.Allowed(url, out var iuri))
        {
            image = await TrackTagger.FetchCoverAsync(iuri!.ToString(), ct, logger).ConfigureAwait(false);
            label = "iTunes";
        }
        else
        {
            return (null, "Choisissez une pochette proposée ou envoyez une image.", false);
        }

        var mime = image is null || image.Length > MaxBytes ? null : CoverHosts.ImageMime(image);
        if (image is null || mime is null)
        {
            return (null, "Image introuvable ou format non pris en charge (JPEG ou PNG).", true);
        }

        return (new CoverImageData(image, mime, label), null, false);
    }
}
