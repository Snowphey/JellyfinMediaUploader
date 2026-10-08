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
