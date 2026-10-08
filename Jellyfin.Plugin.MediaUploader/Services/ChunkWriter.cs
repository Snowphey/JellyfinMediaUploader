namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Écriture d'un morceau reçu à la suite d'un fichier partiel (commun à l'envoi direct et aux lots).
/// </summary>
public static class ChunkWriter
{
    /// <summary>
    /// Ajoute le corps de la requête au fichier partiel, à partir de la position validée.
    /// </summary>
    /// <param name="tempPath">Fichier partiel.</param>
    /// <param name="received">Octets déjà validés.</param>
    /// <param name="size">Taille totale annoncée.</param>
    /// <param name="body">Corps de la requête.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Succès, total d'octets validés, message d'erreur éventuel.</returns>
    public static async Task<(bool Ok, long Received, string? Error)> AppendAsync(string tempPath, long received, long size, Stream body, CancellationToken ct)
    {
        // Un morceau précédent interrompu a pu laisser des octets en trop : on repart de la position validée.
        await using var output = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        output.SetLength(received);
        output.Seek(received, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (received + written > size)
            {
                output.SetLength(received);
                return (false, received, "Le morceau dépasse la taille annoncée.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        return (true, received + written, null);
    }
}
