namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Ce qu'on retient d'un morceau pour le ranger : artiste, album, numéros.
/// </summary>
/// <param name="Artist">Artiste du morceau.</param>
/// <param name="AlbumArtist">Artiste de l'album (sert de dossier).</param>
/// <param name="Album">Album.</param>
/// <param name="Track">Numéro de piste.</param>
/// <param name="Disc">Numéro de disque.</param>
/// <param name="Year">Année.</param>
public sealed record TrackMeta(string Artist, string AlbumArtist, string Album, int? Track, int? Disc, int? Year);

/// <summary>
/// Choix de rangement des morceaux importés.
/// </summary>
public static class ImportNaming
{
    /// <summary>Nom de l'artiste de dossier des playlists.</summary>
    public const string VariousArtists = "Various Artists";

    /// <summary>
    /// Normalise le choix de rangement.
    /// </summary>
    /// <param name="value">Valeur reçue.</param>
    /// <returns>"source" (album d'origine de chaque morceau) ou "playlist" (tout dans un album au nom de la liste).</returns>
    public static string NormalizeLayout(string? value) => string.Equals(value?.Trim(), "playlist", StringComparison.OrdinalIgnoreCase) ? "playlist" : "source";

    /// <summary>
    /// Détermine artiste, album et numéros d'un morceau. Un morceau sans album (vidéo YouTube) rejoint toujours l'album de la liste.
    /// </summary>
    /// <param name="t">Morceau.</param>
    /// <param name="listTitle">Titre de la liste (playlist ou album).</param>
    /// <param name="layout">"source" ou "playlist".</param>
    /// <param name="position">Position du morceau dans la liste (à partir de 1).</param>
    /// <returns>Métadonnées retenues.</returns>
    public static TrackMeta Resolve(TrackSpec t, string listTitle, string layout, int position)
    {
        var artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist.Trim();
        if (layout == "playlist" || string.IsNullOrWhiteSpace(t.Album))
        {
            var album = string.IsNullOrWhiteSpace(listTitle) ? "Import" : listTitle.Trim();
            return new TrackMeta(artist, VariousArtists, album, position, null, t.Year);
        }

        var albumArtist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist.Trim() : artist;
        return new TrackMeta(artist, albumArtist, t.Album.Trim(), t.TrackNo ?? position, t.DiscNo, t.Year);
    }

    /// <summary>
    /// Chemin « côté client » donné au fichier : le planificateur s'en sert pour le nom final et le contexte de dossier.
    /// </summary>
    /// <param name="meta">Métadonnées retenues.</param>
    /// <param name="title">Titre.</param>
    /// <param name="extension">Extension avec le point.</param>
    /// <returns>Chemin « Artiste/Album/05 - Titre.m4a ».</returns>
    public static string ClientPath(TrackMeta meta, string title, string extension)
    {
        return PathBuilder.SanitizeSegment(meta.AlbumArtist, "Unknown Artist") + "/"
            + PathBuilder.SanitizeSegment(meta.Album, "Unknown Album") + "/"
            + TrackNaming.FileName(meta.Disc, meta.Track, title, extension);
    }
}
