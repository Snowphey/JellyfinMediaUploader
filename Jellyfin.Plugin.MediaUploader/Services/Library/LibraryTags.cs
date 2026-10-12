namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Tags d'un fichier audio, pour l'éditeur.</summary>
/// <param name="Title">Titre.</param>
/// <param name="Artist">Interprètes (séparés par « ; »).</param>
/// <param name="AlbumArtist">Artiste de l'album.</param>
/// <param name="Album">Album.</param>
/// <param name="Year">Année (0 : absente).</param>
/// <param name="Track">Numéro de piste (0 : absent).</param>
/// <param name="Disc">Numéro de disque (0 : absent).</param>
/// <param name="Genre">Genre.</param>
/// <param name="HasCover">Vrai si une pochette est intégrée au fichier.</param>
public sealed record TagSnapshot(string? Title, string? Artist, string? AlbumArtist, string? Album, uint Year, uint Track, uint Disc, string? Genre, bool HasCover);

/// <summary>Modifications de tags : un champ à null n'est pas touché, une chaîne vide efface le champ.</summary>
public sealed class TagChanges
{
    /// <summary>Gets or sets le titre.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets les interprètes (séparés par « ; »).</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'artiste de l'album.</summary>
    public string? AlbumArtist { get; set; }

    /// <summary>Gets or sets l'album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets le genre.</summary>
    public string? Genre { get; set; }

    /// <summary>Gets or sets l'année (0 : effacer).</summary>
    public uint? Year { get; set; }

    /// <summary>Gets or sets le numéro de piste (0 : effacer).</summary>
    public uint? Track { get; set; }

    /// <summary>Gets or sets le numéro de disque (0 : effacer).</summary>
    public uint? Disc { get; set; }

    /// <summary>Gets a value indicating whether aucun champ n'est modifié.</summary>
    public bool IsEmpty => Title is null && Artist is null && AlbumArtist is null && Album is null && Genre is null && Year is null && Track is null && Disc is null;
}

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
                t.Year,
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
                t.Performers = Split(c.Artist);
            }

            if (c.AlbumArtist is not null)
            {
                t.AlbumArtists = Split(c.AlbumArtist);
            }

            if (c.Album is not null)
            {
                t.Album = c.Album.Length == 0 ? null : c.Album;
            }

            if (c.Genre is not null)
            {
                t.Genres = Split(c.Genre);
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

    private static string[] Split(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
