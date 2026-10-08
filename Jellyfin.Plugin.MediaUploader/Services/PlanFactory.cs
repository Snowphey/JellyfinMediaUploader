using System.Text.Json;
using Jellyfin.Plugin.MediaUploader.Configuration;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Fabrique le moteur de détection et les réglages du planificateur à partir de la configuration du plugin.
/// </summary>
public static class PlanFactory
{
    private static readonly object Gate = new();
    private static string? _signature;
    private static DetectionEngine? _engine;

    /// <summary>
    /// Règles en vigueur : celles de l'administrateur si la liste a été personnalisée, sinon celles par défaut.
    /// </summary>
    /// <param name="c">Configuration.</param>
    /// <returns>Règles.</returns>
    public static List<DetectionRule> EffectiveRules(PluginConfiguration c)
    {
        return c.RulesCustomized ? c.Rules.ToList() : DefaultRules.Create();
    }

    /// <summary>
    /// Moteur de détection (mis en cache tant que les règles ne changent pas).
    /// </summary>
    /// <param name="c">Configuration.</param>
    /// <returns>Moteur.</returns>
    public static DetectionEngine Engine(PluginConfiguration c)
    {
        var rules = EffectiveRules(c);
        var signature = JsonSerializer.Serialize(rules) + "|" + c.UseFolderContext;
        lock (Gate)
        {
            if (_engine is null || _signature != signature)
            {
                _engine = new DetectionEngine(rules, c.UseFolderContext);
                _signature = signature;
            }

            return _engine;
        }
    }

    /// <summary>
    /// Réglages du planificateur.
    /// </summary>
    /// <param name="c">Configuration.</param>
    /// <returns>Réglages.</returns>
    public static PlannerOptions Options(PluginConfiguration c)
    {
        bool Structured(string? layout) => !string.Equals(layout ?? "auto", "flat", StringComparison.OrdinalIgnoreCase);
        return new PlannerOptions
        {
            MusicRoot = c.MusicPath?.Trim() ?? string.Empty,
            MoviesRoot = c.MoviesPath?.Trim() ?? string.Empty,
            ShowsRoot = c.ShowsPath?.Trim() ?? string.Empty,
            MusicStructured = Structured(c.MusicLayout),
            MoviesStructured = Structured(c.MoviesLayout),
            ShowsStructured = Structured(c.ShowsLayout),
            Audio = ParseExtensions(c.AudioExtensions),
            Video = ParseExtensions(c.VideoExtensions),
            Extra = ParseExtensions(c.ExtraExtensions),
            Overwrite = c.OverwriteExisting,
            MaxFileBytes = c.MaxFileSizeMb > 0 ? c.MaxFileSizeMb * 1024L * 1024L : 0,
            RenameSubtitles = c.RenameSubtitles
        };
    }

    /// <summary>
    /// Accès au disque, avec lecture des tags (détection des doublons de musique dans la bibliothèque).
    /// </summary>
    /// <returns>Sonde.</returns>
    public static DiskFileProbe Probe() => new(p => AudioTagReader.ReadInfo(p, Path.GetExtension(p).ToLowerInvariant()));

    /// <summary>
    /// Découpe une liste d'extensions séparées par des virgules.
    /// </summary>
    /// <param name="value">Liste.</param>
    /// <returns>Extensions en minuscules, avec le point.</returns>
    public static HashSet<string> ParseExtensions(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
    }

    /// <summary>
    /// Normalise le type demandé.
    /// </summary>
    /// <param name="value">Valeur brute.</param>
    /// <returns>"auto", "music", "movie" ou "series" ; null si la valeur est invalide.</returns>
    public static string? NormalizeMode(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "auto" => "auto",
            "music" => "music",
            "movie" or "movies" or "film" => "movie",
            "series" or "show" or "shows" or "anime" => "series",
            _ => null
        };
    }
}
