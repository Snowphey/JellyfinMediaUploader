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
/// <param name="Guessed">La source ne donnait pas d'album : single « Artiste/Titre » en attendant MusicBrainz (album d'origine).</param>
public sealed record TrackMeta(string Artist, string AlbumArtist, string Album, int? Track, int? Disc, int? Year, bool Guessed = false);

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
    /// Détermine artiste, album et numéros d'un morceau. « playlist » : tout dans un album au nom de la liste. « source » : l'album
    /// d'origine ; sans album (vidéo YouTube, liste Spotify), un single « Artiste/Titre » que MusicBrainz complète au téléchargement.
    /// </summary>
    /// <param name="t">Morceau.</param>
    /// <param name="listTitle">Titre de la liste (playlist ou album).</param>
    /// <param name="layout">"source" ou "playlist".</param>
    /// <param name="position">Position du morceau dans la liste (à partir de 1).</param>
    /// <returns>Métadonnées retenues.</returns>
    public static TrackMeta Resolve(TrackSpec t, string listTitle, string layout, int position)
    {
        var artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist.Trim();
        if (layout == "playlist")
        {
            var album = string.IsNullOrWhiteSpace(listTitle) ? "Import" : listTitle.Trim();
            return new TrackMeta(artist, VariousArtists, album, position, null, t.Year);
        }

        if (string.IsNullOrWhiteSpace(t.Album))
        {
            // Pas d'album connu : un single au nom du morceau, rangé sous l'artiste principal (« A, B » : A).
            var lead = ArtistCredits.Split(artist, null).Lead;
            return new TrackMeta(artist, lead, MusicBrainzCredits.SearchTitle(t.Title), 1, null, t.Year, Guessed: true);
        }

        var albumArtist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist.Trim() : artist;
        return new TrackMeta(artist, albumArtist, t.Album.Trim(), t.TrackNo ?? position, t.DiscNo, t.Year);
    }

    /// <summary>
    /// Complète un morceau sans album d'après MusicBrainz : album d'origine, piste, disque, année, interprètes séparés
    /// (collaborations comprises), artiste du dossier. Sans sortie retenue, le morceau reste un single.
    /// </summary>
    /// <param name="meta">Métadonnées du single provisoire.</param>
    /// <param name="mb">Réponse de MusicBrainz.</param>
    /// <param name="title">Titre.</param>
    /// <returns>Métadonnées retenues (artistes séparés par « ; »).</returns>
    public static TrackMeta FromMusicBrainz(TrackMeta meta, MbCredit mb, string title)
    {
        var artists = MusicBrainzCredits.WithTagArtists(mb, ArtistCredits.Split(meta.Artist, null).Artists);
        return mb.Release is null
            ? meta with { Artist = string.Join("; ", artists), AlbumArtist = mb.FolderArtist, Year = mb.Year > 0 ? mb.Year : meta.Year, Guessed = false }
            : new TrackMeta(string.Join("; ", artists), mb.FolderArtist, mb.Release, mb.Track > 0 ? mb.Track : 1, mb.Disc > 1 ? mb.Disc : null, mb.Year > 0 ? mb.Year : meta.Year);
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
