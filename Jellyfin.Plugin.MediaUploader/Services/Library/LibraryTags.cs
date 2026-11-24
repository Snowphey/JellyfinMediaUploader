namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Lecture et écriture des tags des fichiers audio de la bibliothèque (TagLibSharp : ID3, MP4, Vorbis…).
/// </summary>
public static class LibraryTags
{
    /// <summary>
    /// Lit les tags d'un fichier. Ne lève jamais d'exception.
    /// </summary>
    /// <param name="path">Chemin complet.</param>
    /// <returns>Tags, ou null si le fichier est illisible.</returns>
    public static TagSnapshot? Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var t = file.Tag;
            return new TagSnapshot(
                Clean(t.Title),
                t.Performers is { Length: > 0 } ? string.Join("; ", t.Performers) : null,
                t.AlbumArtists is { Length: > 0 } ? string.Join("; ", t.AlbumArtists) : null,
                Clean(t.Album),
                NameTools.NormalizeYear(t.Year),
                t.Track,
                t.Disc,
                Clean(t.FirstGenre),
                t.Pictures is { Length: > 0 });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Indique si le fichier contient une pochette intégrée. Ne lève jamais d'exception.
    /// </summary>
    /// <param name="path">Chemin complet.</param>
    /// <returns>Vrai si une pochette est présente.</returns>
    public static bool HasCover(string path) => Read(path)?.HasCover ?? false;

    /// <summary>
    /// Écrit des modifications de tags dans un fichier (la pochette et les autres tags sont conservés).
    /// </summary>
    /// <param name="path">Chemin complet.</param>
    /// <param name="c">Modifications.</param>
    /// <returns>Null si tout s'est bien passé, sinon le message d'erreur.</returns>
    public static string? Apply(string path, TagChanges c)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var t = file.Tag;
            if (c.Title is not null)
            {
                t.Title = c.Title.Length == 0 ? null : c.Title;
            }

            if (c.Artist is not null)
            {
                t.Performers = OneValue(c.Artist);
            }

            if (c.AlbumArtist is not null)
            {
                t.AlbumArtists = OneValue(c.AlbumArtist);
            }

            if (c.Album is not null)
            {
                t.Album = c.Album.Length == 0 ? null : c.Album;
            }

            if (c.Genre is not null)
            {
                t.Genres = c.Genre.Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (c.Year is not null)
            {
                t.Year = c.Year.Value;
            }

            if (c.Track is not null)
            {
                t.Track = c.Track.Value;
            }

            if (c.Disc is not null)
            {
                t.Disc = c.Disc.Value;
            }

            file.Save();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Intègre une pochette (JPEG ou PNG) à un fichier audio, en remplaçant celle qui s'y trouve.
    /// </summary>
    /// <param name="path">Chemin complet.</param>
    /// <param name="data">Image.</param>
    /// <param name="mime">"image/jpeg" ou "image/png".</param>
    /// <returns>Null si tout s'est bien passé, sinon le message d'erreur.</returns>
    public static string? SetCover(string path, byte[] data, string mime)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(data)) { Type = TagLib.PictureType.FrontCover, MimeType = mime } };
            file.Save();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // Plusieurs artistes : une seule valeur « A; B », que Jellyfin sépare avec le séparateur « ; ». Plusieurs valeurs seraient écrites « A/B »
    // dans un mp3 (ID3v2.3), que Jellyfin ne sépare pas sans « / » (exclu pour « AC/DC », « K/DA »).
    private static string[] OneValue(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? Array.Empty<string>() : new[] { string.Join("; ", parts) };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
