namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// État d'un envoi par morceaux en cours (gardé en mémoire ; le fichier partiel est un .part dans la bibliothèque).
/// </summary>
public sealed class ChunkSession
{
    /// <summary>Gets l'identifiant de l'envoi.</summary>
    public required string Id { get; init; }

    /// <summary>Gets l'utilisateur (ou la clé API) qui a démarré l'envoi.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets le chemin du fichier partiel.</summary>
    public required string TempPath { get; init; }

    /// <summary>Gets le dossier racine de destination.</summary>
    public required string Root { get; init; }

    /// <summary>Gets le type demandé : "auto", "music", "movie" ou "series".</summary>
    public required string Mode { get; init; }

    /// <summary>Gets le nom d'origine du fichier.</summary>
    public required string OriginalName { get; init; }

    /// <summary>Gets la taille totale annoncée, en octets.</summary>
    public required long Size { get; init; }

    /// <summary>Gets l'artiste imposé.</summary>
    public string? Artist { get; init; }

    /// <summary>Gets l'album imposé.</summary>
    public string? Album { get; init; }

    /// <summary>Gets le titre imposé (film ou série).</summary>
    public string? Title { get; init; }

    /// <summary>Gets l'année imposée.</summary>
    public int? Year { get; init; }

    /// <summary>Gets la saison imposée.</summary>
    public int? Season { get; init; }

    /// <summary>Gets a value indicating whether un doublon de musique est enregistré quand même.</summary>
    public bool? Force { get; init; }

    /// <summary>Gets le scan demandé après envoi (null = valeur de la config).</summary>
    public bool? Scan { get; init; }

    /// <summary>Gets or sets le nombre d'octets reçus et validés.</summary>
    public long Received { get; set; }

    /// <summary>Gets or sets la date de dernière activité (UTC).</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>Gets le verrou qui sérialise les écritures d'un même envoi.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}
