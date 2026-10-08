using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Accès au disque de la bibliothèque, remplaçable dans les tests.
/// </summary>
public interface IFileProbe
{
    /// <summary>Vrai si le fichier existe.</summary>
    /// <param name="path">Chemin complet.</param>
    /// <returns>Résultat.</returns>
    bool Exists(string path);

    /// <summary>Liste les fichiers d'un dossier (non récursif).</summary>
    /// <param name="directory">Dossier.</param>
    /// <returns>Chemins complets (vide si le dossier n'existe pas).</returns>
    IEnumerable<string> ListFiles(string directory);

    /// <summary>Liste les fichiers d'un dossier et de ses sous-dossiers (plafonné).</summary>
    /// <param name="directory">Dossier.</param>
    /// <returns>Chemins complets.</returns>
    IEnumerable<string> ListFilesDeep(string directory);

    /// <summary>Lit les tags d'un fichier audio existant.</summary>
    /// <param name="path">Chemin complet.</param>
    /// <returns>Tags, ou null si illisibles.</returns>
    TagInfo? ReadTags(string path);
}

/// <summary>Implémentation réelle : le disque.</summary>
public sealed class DiskFileProbe : IFileProbe
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, TagInfo? Tags)> TagCache = new();
    private readonly Func<string, TagInfo?>? _tagReader;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="DiskFileProbe"/>.
    /// </summary>
    /// <param name="tagReader">Lecteur de tags (null : pas de détection de doublons dans la bibliothèque).</param>
    public DiskFileProbe(Func<string, TagInfo?>? tagReader = null)
    {
        _tagReader = tagReader;
    }

    /// <inheritdoc />
    public IEnumerable<string> ListFilesDeep(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(5000).ToList() : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <inheritdoc />
    public TagInfo? ReadTags(string path)
    {
        if (_tagReader is null)
        {
            return null;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (TagCache.TryGetValue(path, out var hit) && hit.Stamp == stamp)
            {
                return hit.Tags;
            }

            var tags = _tagReader(path);
            TagCache[path] = (stamp, tags);
            return tags;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public IEnumerable<string> ListFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).ToList() : Enumerable.Empty<string>();
        }
        catch (IOException)
        {
            return Enumerable.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }
}

/// <summary>Sonde sans disque : rien n'existe (aperçus et tests de règles).</summary>
public sealed class EmptyFileProbe : IFileProbe
{
    /// <inheritdoc />
    public bool Exists(string path) => false;

    /// <inheritdoc />
    public IEnumerable<string> ListFiles(string directory) => Enumerable.Empty<string>();

    /// <inheritdoc />
    public IEnumerable<string> ListFilesDeep(string directory) => Enumerable.Empty<string>();

    /// <inheritdoc />
    public TagInfo? ReadTags(string path) => null;
}

/// <summary>
/// Réglages dont le planificateur a besoin (extraits de la configuration du plugin).
/// </summary>
public sealed class PlannerOptions
{
    /// <summary>Gets or sets le dossier musique.</summary>
    public string MusicRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets le dossier films.</summary>
    public string MoviesRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets le dossier séries et animés.</summary>
    public string ShowsRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether la musique est rangée en Artiste/Album.</summary>
    public bool MusicStructured { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether les films sont rangés en Titre (Année)/.</summary>
    public bool MoviesStructured { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether les séries sont rangées en Série/Season NN/.</summary>
    public bool ShowsStructured { get; set; } = true;

    /// <summary>Gets or sets les extensions audio.</summary>
    public HashSet<string> Audio { get; set; } = new();

    /// <summary>Gets or sets les extensions vidéo.</summary>
    public HashSet<string> Video { get; set; } = new();

    /// <summary>Gets or sets les extensions annexes (sous-titres, pochettes, paroles...).</summary>
    public HashSet<string> Extra { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether un fichier existant peut être écrasé.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Gets or sets la taille maximale par fichier en octets (0 = illimitée).</summary>
    public long MaxFileBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether un sous-titre est renommé pour suivre le nom de sa vidéo.</summary>
    public bool RenameSubtitles { get; set; } = true;
}

/// <summary>
/// Un fichier à planifier.
/// </summary>
public sealed class PlanFile
{
    /// <summary>Gets or sets l'identifiant (stable pendant tout le lot).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets le chemin relatif côté client (dossier déposé) ou simplement le nom.</summary>
    public string ClientPath { get; set; } = string.Empty;

    /// <summary>Gets or sets la taille en octets.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets les tags lus (null tant que le fichier n'est pas arrivé ou s'ils sont illisibles).</summary>
    public TagInfo? Tags { get; set; }

    /// <summary>Gets or sets a value indicating whether le contenu du fichier est arrivé et analysé.</summary>
    public bool Analyzed { get; set; }
}

/// <summary>
/// Corrections de l'utilisateur pour un fichier (tous les champs sont facultatifs ; 0 efface une année).
/// </summary>
public sealed class ItemOverride
{
    /// <summary>Gets or sets le type imposé ("movie" ou "series" pour une vidéo).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets le titre.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année (0 = aucune).</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la saison.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets l'épisode.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets l'artiste.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets l'identifiant de la vidéo à laquelle rattacher ce sous-titre.</summary>
    public string? PairWith { get; set; }

    /// <summary>Gets or sets a value indicating whether le sous-titre garde son nom d'origine.</summary>
    public bool KeepName { get; set; }

    /// <summary>Gets or sets a value indicating whether un morceau détecté comme doublon est quand même enregistré.</summary>
    public bool Force { get; set; }
}

/// <summary>
/// Doublon détecté pour un morceau : même artiste et même titre (tags, comparés sans casse, accents ni ponctuation).
/// </summary>
public sealed class PlanDuplicate
{
    /// <summary>Gets or sets le morceau existant : nom du fichier du lot, ou chemin relatif en bibliothèque.</summary>
    public string With { get; set; } = string.Empty;

    /// <summary>Gets or sets l'origine : "batch" ou "library".</summary>
    public string Source { get; set; } = "library";

    /// <summary>Gets or sets a value indicating whether l'utilisateur a forcé l'enregistrement.</summary>
    public bool Forced { get; set; }
}

/// <summary>
/// Association d'un sous-titre à une vidéo.
/// </summary>
public sealed class PlanPair
{
    /// <summary>Gets or sets l'identifiant de la vidéo dans le lot (null si elle est déjà dans la bibliothèque).</summary>
    public string? WithId { get; set; }

    /// <summary>Gets or sets le nom de la vidéo.</summary>
    public string WithName { get; set; } = string.Empty;

    /// <summary>Gets or sets l'origine de la vidéo : "batch", "library" ou "manual".</summary>
    public string Source { get; set; } = "batch";

    /// <summary>Gets or sets a value indicating whether le sous-titre est renommé.</summary>
    public bool Renamed { get; set; }

    /// <summary>Gets or sets a value indicating whether plusieurs vidéos du lot conviennent.</summary>
    public bool Ambiguous { get; set; }
}

/// <summary>
/// Un fichier dans le plan : ce qui a été reconnu et où il ira.
/// </summary>
public sealed class PlanItem
{
    /// <summary>Gets or sets l'identifiant.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets le nom d'origine.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets le chemin d'origine côté client.</summary>
    public string ClientPath { get; set; } = string.Empty;

    /// <summary>Gets or sets la taille.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets le rôle : "main" (audio ou vidéo), "subtitle" ou "extra".</summary>
    public string Role { get; set; } = "main";

    /// <summary>Gets or sets le type : "music", "movie", "series" (null si ignoré).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets le titre.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la saison.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets l'épisode.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets l'artiste.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets la confiance : "sure", "guess" ou "unknown".</summary>
    public string Confidence { get; set; } = "unknown";

    /// <summary>Gets or sets l'état : "ready", "guess", "unknown", "duplicate", "skipped" ou "error".</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Gets or sets la règle qui a reconnu le fichier.</summary>
    public string? RuleName { get; set; }

    /// <summary>Gets or sets a value indicating whether l'utilisateur a corrigé ce fichier.</summary>
    public bool Manual { get; set; }

    /// <summary>Gets or sets a value indicating whether les tags attendent l'arrivée du fichier.</summary>
    public bool TagsPending { get; set; }

    /// <summary>Gets or sets les informations manquantes d'un morceau analysé : "artist" et/ou "album" (ni dans les tags, ni dans les règles, ni corrigées).</summary>
    public List<string> Missing { get; set; } = new();

    /// <summary>Gets or sets les remarques.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>Gets or sets la destination relative au dossier de la bibliothèque (séparateur « / »).</summary>
    public string? Dest { get; set; }

    /// <summary>Gets or sets le nom final du fichier.</summary>
    public string? NewName { get; set; }

    /// <summary>Gets or sets l'association (sous-titres).</summary>
    public PlanPair? Pair { get; set; }

    /// <summary>Gets or sets les artistes possibles pour le dossier quand le choix automatique est incertain (artiste commun d'un album aux artistes variés) ; vide si le choix est sûr.</summary>
    public List<string> ArtistChoices { get; set; } = new();

    /// <summary>Gets or sets le doublon détecté (musique), forcé ou non.</summary>
    public PlanDuplicate? Duplicate { get; set; }

    /// <summary>Gets or sets la clé du groupe.</summary>
    public string? GroupKey { get; set; }

    /// <summary>Gets or sets le chemin complet de destination (jamais envoyé au client).</summary>
    [JsonIgnore]
    public string? DestFull { get; set; }

    /// <summary>Gets or sets le dossier racine de destination (jamais envoyé au client).</summary>
    [JsonIgnore]
    public string? Root { get; set; }

    /// <summary>Gets or sets le suffixe de langue d'un sous-titre (jamais envoyé au client).</summary>
    [JsonIgnore]
    public string LangSuffix { get; set; } = string.Empty;
}

/// <summary>
/// Un groupe d'éléments qui vont ensemble : un album, un film (vidéo + sous-titres), une série.
/// </summary>
public sealed class PlanGroup
{
    /// <summary>Gets or sets la clé du groupe.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets le type : "music", "movie", "series", "root" (racine), "ignored".</summary>
    public string Kind { get; set; } = "movie";

    /// <summary>Gets or sets le libellé.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets le dossier de destination relatif.</summary>
    public string? Folder { get; set; }

    /// <summary>Gets or sets l'état : "ready" ou "review" (au moins un élément incertain) ou "ignored".</summary>
    public string Status { get; set; } = "ready";

    /// <summary>Gets or sets a value indicating whether ce groupe réunit les morceaux sans metadata (artiste et/ou album manquants), de provenances diverses.</summary>
    public bool Missing { get; set; }

    /// <summary>Gets or sets le titre commun (films, séries).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année commune.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets l'artiste commun.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album commun.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets les éléments.</summary>
    public List<PlanItem> Items { get; set; } = new();
}

/// <summary>
/// Totaux du plan.
/// </summary>
public sealed class PlanSummary
{
    /// <summary>Gets or sets le nombre de fichiers.</summary>
    public int Files { get; set; }

    /// <summary>Gets or sets le nombre de groupes.</summary>
    public int Groups { get; set; }

    /// <summary>Gets or sets le nombre de fichiers sûrs.</summary>
    public int Ready { get; set; }

    /// <summary>Gets or sets le nombre de fichiers à vérifier.</summary>
    public int ToReview { get; set; }

    /// <summary>Gets or sets le nombre de fichiers ignorés ou en erreur.</summary>
    public int Rejected { get; set; }
}

/// <summary>
/// Plan complet d'un lot.
/// </summary>
public sealed class Plan
{
    /// <summary>Gets or sets les groupes.</summary>
    public List<PlanGroup> Groups { get; set; } = new();

    /// <summary>Gets or sets les totaux.</summary>
    public PlanSummary Summary { get; set; } = new();
}
