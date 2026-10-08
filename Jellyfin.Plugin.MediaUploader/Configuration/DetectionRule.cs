namespace Jellyfin.Plugin.MediaUploader.Configuration;

/// <summary>
/// Règle de détection configurable : une expression régulière à groupes nommés qui reconnaît un nom (ou un chemin) de fichier.
/// Groupes reconnus : <c>title</c>, <c>year</c>, <c>season</c>, <c>episode</c> (films et séries), <c>artist</c>, <c>album</c> (musique).
/// Les règles sont essayées dans l'ordre de la liste : la première qui reconnaît le fichier l'emporte.
/// Les textes sont nullables pour que la validation automatique de l'API n'écarte pas une chaîne vide ;
/// la validation réelle (<see cref="Services.DetectionEngine.Validate"/>) est faite à l'enregistrement.
/// </summary>
public class DetectionRule
{
    /// <summary>Gets or sets l'identifiant stable de la règle.</summary>
    public string? Id { get; set; } = string.Empty;

    /// <summary>Gets or sets le nom lisible de la règle (affiché dans l'aperçu).</summary>
    public string? Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether la règle est active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets le type produit : "movie", "series" ou "music".</summary>
    public string? Kind { get; set; } = "movie";

    /// <summary>Gets or sets ce sur quoi porte l'expression : "name" (nom du fichier sans extension) ou "path" (chemin relatif du dossier déposé, séparateur « / », sans extension).</summary>
    public string? Target { get; set; } = "name";

    /// <summary>Gets or sets l'expression régulière .NET (insensible à la casse), avec des groupes nommés.</summary>
    public string? Pattern { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether la règle ne s'applique que si l'utilisateur a choisi ce type explicitement (évite de prendre un film pour un épisode).</summary>
    public bool ExplicitOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether le résultat est toujours présenté « à vérifier » dans l'aperçu.</summary>
    public bool Guess { get; set; }

    /// <summary>Gets or sets la saison supposée quand la règle de série n'en capture aucune.</summary>
    public int DefaultSeason { get; set; } = 1;

    /// <summary>Gets or sets une note libre pour l'administrateur.</summary>
    public string? Description { get; set; } = string.Empty;
}
