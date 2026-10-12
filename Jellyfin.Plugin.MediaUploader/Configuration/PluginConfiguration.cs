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
        ShowsPath = string.Empty;
        AudioExtensions = ".mp3,.flac,.m4a,.opus,.ogg,.wav,.aac,.wma,.alac";
        VideoExtensions = ".mkv,.mp4,.avi,.mov,.m4v,.wmv,.webm,.ts";
        ExtraExtensions = ".jpg,.jpeg,.png,.lrc,.nfo,.srt,.ass,.ssa,.vtt,.sub,.sup";
        MusicLayout = "auto";
        MoviesLayout = "auto";
        ShowsLayout = "auto";
        Rules = new List<DetectionRule>();
        RulesCustomized = false;
        UseFolderContext = true;
        RenameSubtitles = true;
        ConfirmationMode = "always";
        BatchTtlHours = 6;
        MaxFileSizeMb = 0;
        ChunkSizeMb = 8;
        AutoScan = true;
        OverwriteExisting = false;
        ExtractCover = true;
        AllowNonAdminUploads = true;
        ImportEnabled = true;
        YtDlpPath = string.Empty;
        ImportAudioFormat = "m4a";
        ImportConcurrency = 1;
        ImportCookiesPath = string.Empty;
        ImportMinDelaySeconds = 8;
        ImportMaxDelaySeconds = 25;
        ImportMaxPerHour = 100;
        ImportMaxPerDay = 400;
        ImportCreatePlaylist = true;
        ImportPlaylistPublic = true;
        LibraryEnabled = true;
        LibraryViewAccess = "admins";
        LibraryMoveAccess = "admins";
        LibraryDeleteAccess = "admins";
        LibraryDeleteMode = "trash";
        LibraryTrashDays = 14;
        LibraryConfirmThreshold = 20;
        LibraryMaxItems = 500;
        LibraryRefresh = "targeted";
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
    /// Obtient ou définit le dossier racine de la bibliothèque séries et animés (type « Séries » dans Jellyfin).
    /// </summary>
    public string ShowsPath { get; set; }

    /// <summary>
    /// Obtient ou définit le rangement musique : "auto" (Artiste/Album déduits des tags ou fournis, sinon à la racine) ou "flat" (toujours à la racine).
    /// </summary>
    public string MusicLayout { get; set; }

    /// <summary>
    /// Obtient ou définit le rangement films : "auto" (Titre (Année)/ déduit du nom de fichier ou fourni, sinon à la racine) ou "flat" (toujours à la racine).
    /// </summary>
    public string MoviesLayout { get; set; }

    /// <summary>
    /// Obtient ou définit le rangement séries : "auto" (Série (Année)/Season NN/ d'après le nom) ou "flat" (toujours à la racine).
    /// </summary>
    public string ShowsLayout { get; set; }

    /// <summary>
    /// Obtient ou définit les règles de détection personnalisées (utilisées seulement si <see cref="RulesCustomized"/> est vrai ; sinon les règles par défaut s'appliquent).
    /// </summary>
    public List<DetectionRule> Rules { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si l'administrateur a personnalisé la liste des règles.
    /// </summary>
    public bool RulesCustomized { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si les noms de dossiers déposés servent à compléter la détection (titre d'une série, film dans « Titre (2019)/ »).
    /// </summary>
    public bool UseFolderContext { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si un sous-titre est renommé pour suivre le nom de sa vidéo (condition pour que Jellyfin l'associe).
    /// </summary>
    public bool RenameSubtitles { get; set; }

    /// <summary>
    /// Obtient ou définit le comportement de la page d'upload après l'analyse : "always" (toujours demander confirmation),
    /// "doubtful" (seulement s'il y a un doute), "never" (envoyer directement).
    /// </summary>
    public string ConfirmationMode { get; set; }

    /// <summary>
    /// Obtient ou définit la durée en heures pendant laquelle un lot analysé mais non confirmé est conservé (1 à 72).
    /// </summary>
    public int BatchTtlHours { get; set; }

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

    /// <summary>
    /// Obtient ou définit une valeur indiquant si l'import par lien (Spotify, YouTube) et la navigation par artistes sont proposés.
    /// </summary>
    public bool ImportEnabled { get; set; }

    /// <summary>
    /// Obtient ou définit le chemin de yt-dlp (vide : outil installé par le plugin, sinon PATH).
    /// </summary>
    public string YtDlpPath { get; set; }

    /// <summary>
    /// Obtient ou définit le format audio des imports : "m4a" (défaut, sans réencodage), "opus" ou "mp3".
    /// </summary>
    public string ImportAudioFormat { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre de téléchargements simultanés (1 à 4).
    /// </summary>
    public int ImportConcurrency { get; set; }

    /// <summary>
    /// Obtient ou définit le délai minimal, en secondes, entre deux téléchargements (tirage aléatoire entre min et max, pour tout le serveur).
    /// </summary>
    public int ImportMinDelaySeconds { get; set; }

    /// <summary>
    /// Obtient ou définit le délai maximal, en secondes, entre deux téléchargements.
    /// </summary>
    public int ImportMaxDelaySeconds { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre maximal de téléchargements par heure glissante pour tout le serveur (0 : sans limite).
    /// </summary>
    public int ImportMaxPerHour { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre maximal de téléchargements par jour glissant pour tout le serveur (0 : sans limite).
    /// </summary>
    public int ImportMaxPerDay { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si un import de playlist crée aussi une liste de lecture Jellyfin (valeur proposée par défaut à chaque import).
    /// </summary>
    public bool ImportCreatePlaylist { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si la liste de lecture créée est publique par défaut (visible de tous les utilisateurs) ou privée.
    /// </summary>
    public bool ImportPlaylistPublic { get; set; }

    /// <summary>
    /// Obtient ou définit une valeur indiquant si l'onglet « Bibliothèque » (gestion des fichiers des dossiers média) est disponible.
    /// </summary>
    public bool LibraryEnabled { get; set; }

    /// <summary>
    /// Obtient ou définit qui peut parcourir la bibliothèque : "admins" (administrateurs) ou "uploaders" (tous ceux qui peuvent envoyer des fichiers).
    /// </summary>
    public string LibraryViewAccess { get; set; }

    /// <summary>
    /// Obtient ou définit qui peut déplacer et renommer : "admins" ou "viewers" (tous ceux qui peuvent parcourir).
    /// </summary>
    public string LibraryMoveAccess { get; set; }

    /// <summary>
    /// Obtient ou définit qui peut supprimer : "none" (personne), "admins", ou "jellyfin" (administrateurs et utilisateurs ayant le droit de supprimer du contenu dans Jellyfin).
    /// </summary>
    public string LibraryDeleteAccess { get; set; }

    /// <summary>
    /// Obtient ou définit le mode de suppression : "trash" (corbeille, restauration possible) ou "permanent" (définitif).
    /// </summary>
    public string LibraryDeleteMode { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre de jours de conservation de la corbeille (0 : jamais purgée automatiquement).
    /// </summary>
    public int LibraryTrashDays { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre de fichiers à partir duquel une suppression demande de taper un mot de confirmation (0 : toujours).
    /// </summary>
    public int LibraryConfirmThreshold { get; set; }

    /// <summary>
    /// Obtient ou définit le nombre maximal d'éléments par action (déplacement ou suppression).
    /// </summary>
    public int LibraryMaxItems { get; set; }

    /// <summary>
    /// Obtient ou définit ce que Jellyfin fait après une action sur la bibliothèque : "targeted" (seuls les dossiers touchés sont actualisés), "full" (scan complet) ou "none".
    /// Les éléments supprimés ou déplacés sont de toute façon retirés de la médiathèque tout de suite, sauf avec "none".
    /// </summary>
    public string LibraryRefresh { get; set; }

    /// <summary>
    /// Obtient ou définit le fichier de cookies (format Netscape) transmis à yt-dlp, par exemple pour YouTube Music Premium. Facultatif.
    /// </summary>
    public string ImportCookiesPath { get; set; }
}
