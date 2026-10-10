using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Crée la liste de lecture Jellyfin d'une playlist importée. Les fichiers viennent d'entrer dans le dossier de la bibliothèque :
/// Jellyfin ne les connaît qu'après son scan, donc on les attend en tâche de fond avant de les ajouter à la liste.
/// </summary>
public static class PlaylistBuilder
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Programme la création (ou le complément) de la liste de lecture.
    /// </summary>
    /// <param name="libraryManager">Bibliothèque Jellyfin.</param>
    /// <param name="playlistManager">Gestionnaire de listes de lecture.</param>
    /// <param name="spec">Liste voulue.</param>
    /// <param name="paths">Chemins des fichiers rangés, dans l'ordre de la playlist d'origine.</param>
    /// <param name="logger">Logger.</param>
    public static void Queue(ILibraryManager libraryManager, IPlaylistManager playlistManager, PlaylistRequest spec, IReadOnlyList<string> paths, ILogger logger)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(libraryManager, playlistManager, spec, paths, logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaUploader: liste de lecture « {Name} » non créée", spec.Name);
            }
        });
    }

    private static async Task RunAsync(ILibraryManager libraryManager, IPlaylistManager playlistManager, PlaylistRequest spec, IReadOnlyList<string> paths, ILogger logger)
    {
        var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow + MaxWait;
        while (true)
        {
            foreach (var path in paths.Where(p => !found.ContainsKey(p)))
            {
                var item = libraryManager.FindByPath(path, false);
                if (item is not null)
                {
                    found[path] = item.Id;
                }
            }

            if (found.Count == paths.Count || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(PollEvery).ConfigureAwait(false);
        }

        var ids = paths.Where(found.ContainsKey).Select(p => found[p]).ToList();
        if (ids.Count == 0)
        {
            logger.LogWarning("MediaUploader: liste de lecture « {Name} » non créée : aucun morceau n'est apparu dans la bibliothèque (scan de la bibliothèque non lancé ou interrompu ?)", spec.Name);
            return;
        }

        // Un seul créateur à la fois : deux confirmations successives du même lot ne doivent pas créer deux listes du même nom.
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = playlistManager.GetPlaylists(spec.UserId)
                .FirstOrDefault(p => p.OwnerUserId == spec.UserId && string.Equals(p.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                await playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = spec.Name,
                    ItemIdList = ids,
                    MediaType = MediaType.Audio,
                    UserId = spec.UserId,
                    Public = spec.Public
                }).ConfigureAwait(false);
                logger.LogInformation("MediaUploader: liste de lecture « {Name} » créée ({Count} morceaux, {Visibility})", spec.Name, ids.Count, spec.Public ? "publique" : "privée");
            }
            else
            {
                var have = existing.LinkedChildren.Select(c => c.ItemId).Where(i => i.HasValue).Select(i => i!.Value).ToHashSet();
                var toAdd = ids.Where(i => !have.Contains(i)).ToList();
                if (toAdd.Count > 0)
                {
                    await playlistManager.AddItemToPlaylistAsync(existing.Id, toAdd, null, spec.UserId).ConfigureAwait(false);
                }

                logger.LogInformation("MediaUploader: liste de lecture « {Name} » complétée ({Count} morceaux ajoutés)", spec.Name, toAdd.Count);
            }

            if (ids.Count < paths.Count)
            {
                logger.LogWarning("MediaUploader: liste de lecture « {Name} » : {Missing} morceau(x) introuvable(s) dans la bibliothèque après {Minutes} min", spec.Name, paths.Count - ids.Count, (int)MaxWait.TotalMinutes);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
