using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// Formulaire d'upload direct (multipart/form-data). Tous les champs sauf <c>files</c> sont optionnels.
/// </summary>
public class UploadRequest
{
    /// <summary>Gets or sets le type de média : "music", "movie" ou "series". Vide = déduit du nom et de l'extension.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets l'artiste (remplace celui des tags).</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album (remplace celui des tags).</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets le titre du film ou de la série (remplace celui déduit du nom de fichier).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année (remplace celle déduite du nom de fichier).</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la saison d'une série (remplace celle déduite du nom de fichier).</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets a value indicating whether un morceau en doublon (même artiste et même titre dans les tags) est enregistré quand même.</summary>
    public bool? Force { get; set; }

    /// <summary>Gets or sets une valeur forçant ou désactivant le scan après upload (null = valeur de la config).</summary>
    public bool? Scan { get; set; }

    /// <summary>Gets or sets les fichiers envoyés.</summary>
    public List<IFormFile> Files { get; set; } = new();
}

/// <summary>
/// Résultat d'un fichier uploadé.
/// </summary>
/// <param name="Name">Nom d'origine.</param>
/// <param name="Status">"saved", "skipped" ou "error".</param>
/// <param name="Path">Chemin final (si enregistré).</param>
/// <param name="Message">Détail éventuel.</param>
/// <param name="Destination">Emplacement relatif au dossier de la bibliothèque (ex. « Artiste/Album/fichier.m4a »).</param>
public record UploadFileResult(string Name, string Status, string? Path, string? Message, string? Destination = null);

/// <summary>
/// Réponse de l'upload.
/// </summary>
/// <param name="Saved">Nombre de fichiers enregistrés.</param>
/// <param name="ScanQueued">Vrai si un scan de bibliothèque a été lancé.</param>
/// <param name="Files">Détail par fichier.</param>
public record UploadResponse(int Saved, bool ScanQueued, IReadOnlyList<UploadFileResult> Files);

/// <summary>
/// État exposé à l'interface.
/// </summary>
/// <param name="MusicConfigured">Dossier musique défini.</param>
/// <param name="MoviesConfigured">Dossier films défini.</param>
/// <param name="AudioExtensions">Extensions audio.</param>
/// <param name="VideoExtensions">Extensions vidéo.</param>
/// <param name="ExtraExtensions">Extensions annexes.</param>
/// <param name="MaxFileSizeMb">Taille max par fichier (Mo, 0 = illimitée).</param>
/// <param name="AutoScan">Scan automatique activé.</param>
/// <param name="CanUpload">L'utilisateur courant a le droit d'uploader.</param>
/// <param name="ChunkSizeMb">Taille des morceaux conseillée à l'interface (Mo).</param>
/// <param name="ShowsConfigured">Dossier séries et animés défini.</param>
/// <param name="ConfirmationMode">"always", "doubtful" ou "never".</param>
/// <param name="BatchTtlHours">Durée de conservation d'un lot non confirmé (heures).</param>
public record StatusResponse(
    bool MusicConfigured,
    bool MoviesConfigured,
    string AudioExtensions,
    string VideoExtensions,
    string ExtraExtensions,
    int MaxFileSizeMb,
    bool AutoScan,
    bool CanUpload,
    int ChunkSizeMb,
    bool ShowsConfigured,
    string ConfirmationMode,
    int BatchTtlHours);

/// <summary>
/// Démarrage d'un envoi direct par morceaux (sans confirmation).
/// </summary>
/// <param name="FileName">Nom du fichier.</param>
/// <param name="Size">Taille totale en octets.</param>
/// <param name="Type">"music", "movie" ou "series" (vide = déduit).</param>
/// <param name="Artist">Artiste (remplace celui des tags).</param>
/// <param name="Album">Album (remplace celui des tags).</param>
/// <param name="Title">Titre du film ou de la série.</param>
/// <param name="Year">Année.</param>
/// <param name="Scan">Scan après envoi (null = valeur de la config).</param>
/// <param name="Season">Saison d'une série.</param>
/// <param name="Force">Enregistre quand même un doublon de musique.</param>
public record StartUploadRequest(string FileName, long Size, string? Type, string? Artist, string? Album, string? Title, int? Year, bool? Scan, int? Season = null, bool? Force = null);

/// <summary>
/// Réponse au démarrage ou à la reprise d'un envoi par morceaux.
/// </summary>
/// <param name="UploadId">Identifiant de l'envoi.</param>
/// <param name="Received">Octets déjà reçus (prochain offset attendu).</param>
/// <param name="Size">Taille totale annoncée.</param>
/// <param name="ChunkSize">Taille de morceau conseillée en octets.</param>
public record ChunkUploadState(string UploadId, long Received, long Size, int ChunkSize);

/// <summary>Un fichier annoncé pour un lot.</summary>
public class BatchFileRequest
{
    /// <summary>Gets or sets le chemin relatif (dossier déposé) ou le nom du fichier.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets la taille en octets.</summary>
    public long Size { get; set; }
}

/// <summary>Création d'un lot : liste des fichiers et type choisi.</summary>
public class CreateBatchRequest
{
    /// <summary>Gets or sets le type : "auto", "music", "movie" ou "series".</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets les fichiers.</summary>
    public List<BatchFileRequest> Files { get; set; } = new();
}

/// <summary>Corrections de l'utilisateur sur un lot.</summary>
public class OverridesRequest
{
    /// <summary>Gets or sets le type de lot (facultatif : change le type choisi).</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets les corrections par identifiant de fichier (remplace les précédentes).</summary>
    public Dictionary<string, ItemOverride>? Overrides { get; set; }
}

/// <summary>Confirmation d'un lot.</summary>
public class CommitRequest
{
    /// <summary>Gets or sets les fichiers à enregistrer.</summary>
    public List<string>? ItemIds { get; set; }

    /// <summary>Gets or sets le scan après enregistrement (null = valeur de la config).</summary>
    public bool? Scan { get; set; }
}

/// <summary>
/// Avancement de l'envoi d'un fichier du lot.
/// </summary>
/// <param name="Received">Octets reçus.</param>
/// <param name="Size">Taille.</param>
/// <param name="Complete">Fichier entièrement reçu et analysé.</param>
public record ItemProgress(long Received, long Size, bool Complete);

/// <summary>
/// État d'un lot : plan et avancement.
/// </summary>
/// <param name="BatchId">Identifiant du lot.</param>
/// <param name="ChunkSize">Taille de morceau conseillée (octets).</param>
/// <param name="Ids">Identifiants attribués aux fichiers, dans l'ordre de la demande.</param>
/// <param name="Plan">Plan de rangement.</param>
/// <param name="Progress">Avancement par fichier.</param>
public record BatchResponse(string BatchId, int ChunkSize, IReadOnlyList<string> Ids, Plan Plan, IReadOnlyDictionary<string, ItemProgress> Progress);

/// <summary>Règles envoyées ou reçues par l'éditeur des paramètres.</summary>
public class RulesPayload
{
    /// <summary>Gets or sets les règles.</summary>
    public List<DetectionRule> Rules { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether revenir aux règles par défaut (ignore <see cref="Rules"/>).</summary>
    public bool Reset { get; set; }
}

/// <summary>Demande de test de règles sur des noms de fichiers.</summary>
public class RulesTestRequest
{
    /// <summary>Gets or sets les noms ou chemins à tester.</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>Gets or sets le type choisi : "auto", "movie", "series" ou "music".</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets les règles à tester (non enregistrées) ; null = règles actuelles.</summary>
    public List<DetectionRule>? Rules { get; set; }
}
