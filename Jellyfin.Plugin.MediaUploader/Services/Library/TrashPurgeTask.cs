using Jellyfin.Plugin.MediaUploader.Api;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Tâche planifiée de Jellyfin (Tableau de bord > Tâches planifiées) : vide chaque nuit les éléments de la corbeille plus vieux que la durée réglée (0 : jamais).
/// Chaque purge est écrite dans le journal d'activité.
/// </summary>
public class TrashPurgeTask : IScheduledTask
{
    private readonly ILogger<TrashPurgeTask> _logger;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="TrashPurgeTask"/>.
    /// </summary>
    /// <param name="activityManager">Journal d'activité de Jellyfin.</param>
    /// <param name="logger">Logger.</param>
    public TrashPurgeTask(IActivityManager activityManager, ILogger<TrashPurgeTask> logger)
    {
        _logger = logger;
        PluginActivity.Attach(activityManager, logger);
    }

    /// <inheritdoc />
    public string Name => "Vider la corbeille de la bibliothèque (Media Uploader)";

    /// <inheritdoc />
    public string Key => "MediaUploaderTrashPurge";

    /// <inheritdoc />
    public string Description => "Supprime définitivement les éléments de la corbeille plus vieux que la durée de conservation réglée dans les paramètres du plugin.";

    /// <inheritdoc />
    public string Category => "Media Uploader";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.LibraryEnabled || config.LibraryTrashDays <= 0)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        var roots = LibraryFs.Roots(config.MusicPath, config.MoviesPath, config.ShowsPath);
        var purged = LibraryFs.PurgeOlderThan(roots, config.LibraryTrashDays, DateTime.UtcNow);
        if (purged.Count > 0)
        {
            _logger.LogInformation("MediaUploader: {Count} élément(s) purgé(s) de la corbeille", purged.Count);
            PluginActivity.Log(
                $"Corbeille vidée automatiquement : {purged.Count} élément{(purged.Count > 1 ? "s" : string.Empty)} de plus de {config.LibraryTrashDays} jours",
                "MediaUploaderLibrary",
                Guid.Empty,
                $"{purged.Sum(p => p.Files)} fichier(s), {ActivityText.Size(purged.Sum(p => p.Bytes))}",
                ActivityText.LibraryDetail(purged.Select(p => $"{p.OriginalPath} (supprimé par {p.DeletedBy} le {p.DeletedAt:dd/MM/yyyy})").ToList()),
                LogLevel.Information);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }
}
