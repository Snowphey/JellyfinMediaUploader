using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaUploader.Configuration;

/// <summary>
/// Configuration du plugin (stockée en XML dans le dossier de config de Jellyfin).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="PluginConfiguration"/>.
    /// </summary>
    public PluginConfiguration()
    {
        MusicPath = string.Empty;
        MoviesPath = string.Empty;
        AudioExtensions = ".mp3,.flac,.m4a,.opus,.ogg,.wav,.aac,.wma,.alac";
        VideoExtensions = ".mkv,.mp4,.avi,.mov,.m4v,.wmv,.webm,.ts";
        ExtraExtensions = ".jpg,.jpeg,.png,.lrc,.srt,.ass,.nfo";
        MusicLayout = "auto";
        MoviesLayout = "auto";
        MaxFileSizeMb = 0;
        ChunkSizeMb = 8;
        AutoScan = true;
        OverwriteExisting = false;
        ExtractCover = true;
        AllowNonAdminUploads = true;
    }

    /// <summary>
    /// Obtient ou définit le dossier racine de la bibliothèque musique.
    /// </summary>
    public string MusicPath { get; set; }

    /// <summary>
    /// Obtient ou définit le dossier racine de la bibliothèque films.
    /// </summary>
    public string MoviesPath { get; set; }

    /// <summary>
    /// Obtient ou définit le rangement musique : "auto" (Artiste/Album déduits des tags ou fournis, sinon à la racine) ou "flat" (toujours à la racine).
    /// </summary>
    public string MusicLayout { get; set; }

    /// <summary>
    /// Obtient ou définit le rangement films : "auto" (Titre (Année)/ déduit du nom de fichier ou fourni, sinon à la racine) ou "flat" (toujours à la racine).
    /// </summary>
    public string MoviesLayout { get; set; }

    /// <summary>
    /// Obtient ou définit les extensions audio acceptées (séparées par des virgules).
    /// </summary>
    public string AudioExtensions { get; set; }

    /// <summary>
    /// Obtient ou définit les extensions vidéo acceptées (séparées par des virgules).
    /// </summary>
    public string VideoExtensions { get; set; }

    /// <summary>
    /// Obtient ou définit les extensions annexes acceptées (pochettes, sous-titres, paroles).
    /// </summary>
    public string ExtraExtensions { get; set; }

    /// <summary>
    /// Obtient ou définit la taille maximale par fichier en Mo (0 = illimitée).
    /// </summary>
    public int MaxFileSizeMb { get; set; }

    /// <summary>
    /// Obtient ou définit la taille des morceaux envoyés par l'interface web en Mo (1 à 90). À réduire si un proxy limite la taille des requêtes.
    /// </summary>
    public int ChunkSizeMb { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si un scan de bibliothèque est lancé après upload.
    /// </summary>
    public bool AutoScan { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si un fichier existant peut être écrasé.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si la pochette intégrée est extraite (cover.jpg) dans le dossier de l'album.
    /// </summary>
    public bool ExtractCover { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si les utilisateurs non administrateurs peuvent uploader.
    /// </summary>
    public bool AllowNonAdminUploads { get; set; }
}
