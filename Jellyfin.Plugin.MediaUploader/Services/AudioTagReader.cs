namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Infos lues dans les tags d'un fichier audio.
/// </summary>
/// <param name="Artist">Artiste d'album, à défaut premier interprète.</param>
/// <param name="Album">Album.</param>
/// <param name="Title">Titre du morceau.</param>
/// <param name="CoverData">Pochette intégrée (octets), si présente.</param>
/// <param name="CoverExtension">Extension de la pochette (.jpg, .png, .webp).</param>
public record AudioTags(string? Artist, string? Album, string? Title, byte[]? CoverData, string? CoverExtension);

/// <summary>
/// Détail des tags d'un fichier audio, pour l'aperçu avant confirmation.
/// </summary>
/// <param name="Title">Titre.</param>
/// <param name="Artist">Interprètes (séparés par « , »).</param>
/// <param name="AlbumArtist">Artiste de l'album.</param>
/// <param name="Album">Album.</param>
/// <param name="Track">Numéro de piste (0 : absent).</param>
/// <param name="Disc">Numéro de disque (0 : absent).</param>
/// <param name="Year">Année (0 : absente).</param>
/// <param name="DurationSec">Durée en secondes.</param>
/// <param name="CoverData">Pochette intégrée, si présente.</param>
/// <param name="CoverMime">Type de la pochette.</param>
public record AudioDetails(string? Title, string? Artist, string? AlbumArtist, string? Album, uint Track, uint Disc, uint Year, int DurationSec, byte[]? CoverData, string? CoverMime);

/// <summary>
/// Lecture des tags audio (ID3, Vorbis, MP4...) avec TagLibSharp.
/// </summary>
public static class AudioTagReader
{
    /// <summary>
    /// Lit les tags d'un fichier. Ne lève jamais d'exception : retourne null si illisible.
    /// </summary>
    /// <param name="path">Chemin du fichier (peut avoir une extension quelconque).</param>
    /// <param name="originalExtension">Extension d'origine, qui sert à TagLib pour reconnaître le format.</param>
    /// <returns>Tags, ou null.</returns>
    public static AudioTags? Read(string path, string originalExtension)
    {
        try
        {
            var abstraction = new NamedFile(path, "file" + originalExtension);
            using var file = TagLib.File.Create(abstraction, TagLib.ReadStyle.None);
            var tag = file.Tag;

            var artist = Clean(tag.FirstAlbumArtist) ?? Clean(tag.FirstPerformer);
            var album = Clean(tag.Album);
            var title = Clean(tag.Title);

            byte[]? cover = null;
            string? coverExt = null;
            if (tag.Pictures is { Length: > 0 })
            {
                var pic = tag.Pictures[0];
                coverExt = pic.MimeType switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => null
                };

                if (coverExt is not null && pic.Data is { Count: > 0 })
                {
                    cover = pic.Data.Data;
                }
            }

            return new AudioTags(artist, album, title, cover, coverExt);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Lit tous les tags utiles à l'aperçu (titre, artistes, album, numéros, année, durée, pochette). Ne lève jamais d'exception.
    /// </summary>
    /// <param name="path">Chemin du fichier.</param>
    /// <param name="originalExtension">Extension d'origine.</param>
    /// <returns>Détail, ou null si le fichier est illisible.</returns>
    public static AudioDetails? ReadDetails(string path, string originalExtension)
    {
        try
        {
            var abstraction = new NamedFile(path, "file" + originalExtension);
            using var file = TagLib.File.Create(abstraction, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            byte[]? cover = null;
            string? mime = null;
            if (tag.Pictures is { Length: > 0 } && tag.Pictures[0] is { Data.Count: > 0 } pic && pic.MimeType is "image/jpeg" or "image/jpg" or "image/png" or "image/webp")
            {
                cover = pic.Data.Data;
                mime = pic.MimeType == "image/jpg" ? "image/jpeg" : pic.MimeType;
            }

            return new AudioDetails(
                Clean(tag.Title),
                tag.Performers is { Length: > 0 } ? string.Join(", ", tag.Performers) : null,
                Clean(tag.FirstAlbumArtist),
                Clean(tag.Album),
                tag.Track,
                tag.Disc,
                tag.Year,
                (int)Math.Round(file.Properties?.Duration.TotalSeconds ?? 0),
                cover,
                mime);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Lit seulement artiste, album et titre (sans la pochette).
    /// </summary>
    /// <param name="path">Chemin du fichier.</param>
    /// <param name="originalExtension">Extension d'origine.</param>
    /// <returns>Tags, ou null si illisibles.</returns>
    public static TagInfo? ReadInfo(string path, string originalExtension)
    {
        var tags = Read(path, originalExtension);
        return tags is null ? null : new TagInfo(tags.Artist, tags.Album, tags.Title);
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // Permet à TagLib de deviner le format via un nom virtuel, alors que le fichier réel a un nom temporaire.
    private sealed class NamedFile : TagLib.File.IFileAbstraction
    {
        private readonly string _path;

        public NamedFile(string path, string virtualName)
        {
            _path = path;
            Name = virtualName;
        }

        public string Name { get; }

        public Stream ReadStream => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        public Stream WriteStream => new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        public void CloseStream(Stream stream)
        {
            stream.Dispose();
        }
    }
}
