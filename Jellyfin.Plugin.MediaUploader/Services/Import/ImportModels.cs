namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Un morceau à télécharger, avec ce qu'on sait de lui avant le téléchargement.
/// </summary>
public sealed class TrackSpec
{
    /// <summary>Gets l'identifiant du morceau dans son import (« t1 », « t2 »…).</summary>
    public required string Id { get; init; }

    /// <summary>Gets le titre.</summary>
    public required string Title { get; init; }

    /// <summary>Gets l'artiste (ou les artistes, séparés par « , »).</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets l'artiste de l'album.</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Gets l'album.</summary>
    public string? Album { get; init; }

    /// <summary>Gets le numéro de piste.</summary>
    public int? TrackNo { get; init; }

    /// <summary>Gets le numéro de disque.</summary>
    public int? DiscNo { get; init; }

    /// <summary>Gets l'année.</summary>
    public int? Year { get; init; }

    /// <summary>Gets la durée en secondes.</summary>
    public int? DurationSec { get; init; }

    /// <summary>Gets l'adresse de la pochette.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>Gets l'adresse de la vidéo à télécharger (sinon, le morceau est cherché sur YouTube par titre et artiste).</summary>
    public string? VideoUrl { get; init; }

    /// <summary>
    /// Copie du morceau sous un autre identifiant (quand plusieurs albums sont regroupés dans un même import).
    /// </summary>
    /// <param name="id">Nouvel identifiant.</param>
    /// <returns>Copie.</returns>
    public TrackSpec WithId(string id) => new()
    {
        Id = id,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        TrackNo = TrackNo,
        DiscNo = DiscNo,
        Year = Year,
        DurationSec = DurationSec,
        CoverUrl = CoverUrl,
        VideoUrl = VideoUrl
    };
}

/// <summary>
/// Contenu d'un lien ou d'une sélection d'albums, une fois résolu.
/// </summary>
public sealed class Listing
{
    /// <summary>Gets or sets le titre (playlist, album, ou artiste pour une sélection d'albums).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets le genre de contenu : "playlist", "album" ou "track".</summary>
    public string Kind { get; set; } = "playlist";

    /// <summary>Gets or sets une remarque à montrer avant le téléchargement (liste tronquée par la source, par exemple).</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets la pochette de l'ensemble.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets les morceaux.</summary>
    public List<TrackSpec> Tracks { get; } = new();
}

/// <summary>
/// Échec d'un import, avec un message destiné à l'utilisateur.
/// </summary>
public sealed class ImportException : Exception
{
    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="ImportException"/>.
    /// </summary>
    /// <param name="message">Message lisible.</param>
    public ImportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="ImportException"/> avec le détail technique (écrit dans le journal de Jellyfin).
    /// </summary>
    /// <param name="message">Message lisible.</param>
    /// <param name="details">Sortie d'erreur de l'outil.</param>
    public ImportException(string message, string details)
        : base(message)
    {
        Details = details;
    }

    /// <summary>Gets le détail technique (sortie d'erreur de l'outil), pour le journal.</summary>
    public string? Details { get; }
}
