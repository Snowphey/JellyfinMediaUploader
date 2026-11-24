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

    /// <summary>
    /// Modifications qui remettent des tags tels qu'ils étaient (annulation).
    /// </summary>
    /// <param name="t">Tags d'origine.</param>
    /// <returns>Modifications.</returns>
    public static TagChanges Restore(TagSnapshot t) => new()
    {
        Title = t.Title ?? string.Empty,
        Artist = t.Artist ?? string.Empty,
        AlbumArtist = t.AlbumArtist ?? string.Empty,
        Album = t.Album ?? string.Empty,
        Genre = t.Genre ?? string.Empty,
        Year = t.Year,
        Track = t.Track,
        Disc = t.Disc
    };

    /// <summary>
    /// Copie des modifications.
    /// </summary>
    /// <returns>Copie.</returns>
    public TagChanges Copy() => (TagChanges)MemberwiseClone();
}
