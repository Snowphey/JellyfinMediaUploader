using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Ajoute les actions du plugin (envois, imports, annulations, installation d'outils) au journal d'activité de Jellyfin
/// (Tableau de bord > Activité). Une écriture n'est jamais bloquante : si elle échoue, l'action en cours continue.
/// </summary>
public static class PluginActivity
{
    private static IActivityManager? _manager;
    private static ILogger? _logger;

    /// <summary>
    /// Branche le journal d'activité (appelé par les contrôleurs, qui reçoivent le service de Jellyfin).
    /// </summary>
    /// <param name="manager">Journal d'activité de Jellyfin.</param>
    /// <param name="logger">Logger pour signaler un échec d'écriture.</param>
    public static void Attach(IActivityManager manager, ILogger logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <summary>
    /// Écrit une ligne dans le journal d'activité.
    /// </summary>
    /// <param name="name">Titre de la ligne (« Alice a envoyé 12 fichiers »).</param>
    /// <param name="type">Type d'événement.</param>
    /// <param name="userId">Utilisateur concerné (Guid.Empty pour le système ou une clé d'API).</param>
    /// <param name="shortOverview">Résumé affiché sous le titre.</param>
    /// <param name="overview">Détail (liste des fichiers).</param>
    /// <param name="severity">Gravité.</param>
    public static void Log(string name, string type, Guid userId, string? shortOverview, string? overview, LogLevel severity = LogLevel.Information)
    {
        var manager = _manager;
        if (manager is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await manager.CreateAsync(new ActivityLog(name, type, userId)
                {
                    ShortOverview = shortOverview,
                    Overview = overview,
                    LogSeverity = severity,
                    DateCreated = DateTime.UtcNow
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MediaUploader: ligne d'activité non écrite ({Name})", name);
            }
        });
    }

    /// <summary>
    /// Identifiant Jellyfin d'un propriétaire d'envoi ou d'import (Guid.Empty pour une clé d'API).
    /// </summary>
    /// <param name="owner">Identifiant texte.</param>
    /// <returns>Identifiant.</returns>
    public static Guid UserIdOf(string? owner) => Guid.TryParse(owner, out var id) ? id : Guid.Empty;
}
