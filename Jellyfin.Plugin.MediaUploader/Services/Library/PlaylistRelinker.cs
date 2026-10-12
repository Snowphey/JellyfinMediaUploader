using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Jellyfin identifie un média par son chemin : un fichier déplacé ou renommé devient pour lui un nouvel élément, et les listes de lecture qui contenaient l'ancien le perdent.
/// Après une action de la page « Bibliothèque », cette classe retrouve le nouvel élément (une fois la médiathèque actualisée) et le remet dans les listes de lecture, à la même place.
/// </summary>
public static class PlaylistRelinker
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(6);

    private sealed record Entry(Guid PlaylistId, Guid OwnerId, Guid OldItemId, int Index);

    /// <summary>
    /// Programme la réparation des listes de lecture pour des éléments qui vont changer de chemin.
    /// </summary>
    /// <param name="libraryManager">Bibliothèque Jellyfin.</param>
    /// <param name="playlistManager">Gestionnaire de listes de lecture.</param>
    /// <param name="userManager">Gestionnaire d'utilisateurs (toutes les listes de lecture sont examinées).</param>
    /// <param name="oldItems">Éléments connus de Jellyfin avant l'action : identifiant et ancien chemin complet.</param>
    /// <param name="newPathOf">Nouveau chemin complet d'un ancien chemin (null : l'élément n'existe plus).</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Nombre d'entrées de listes de lecture concernées (0 : rien à faire).</returns>
    public static int Queue(ILibraryManager libraryManager, IPlaylistManager playlistManager, IUserManager userManager, IReadOnlyDictionary<Guid, string> oldItems, Func<string, string?> newPathOf, ILogger logger)
    {
        if (oldItems.Count == 0)
        {
            return 0;
        }

        // Photographie des listes de lecture contenant ces éléments, prise avant que Jellyfin ne les « oublie ».
        var entries = new List<Entry>();
        var seen = new HashSet<Guid>();
        foreach (var userId in userManager.GetUsersIds())
        {
            foreach (var playlist in playlistManager.GetPlaylists(userId))
            {
                if (!seen.Add(playlist.Id))
                {
                    continue;
                }

                var children = playlist.LinkedChildren;
                for (var i = 0; i < children.Length; i++)
                {
                    if (children[i].ItemId is { } id && oldItems.ContainsKey(id))
                    {
                        entries.Add(new Entry(playlist.Id, playlist.OwnerUserId, id, i));
                    }
                }
            }
        }

        if (entries.Count == 0)
        {
            return 0;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var pending = entries.ToList();
                var deadline = DateTime.UtcNow + MaxWait;
                while (pending.Count > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    foreach (var entry in pending.ToList())
                    {
                        var newPath = newPathOf(oldItems[entry.OldItemId]);
                        if (newPath is null)
                        {
                            pending.Remove(entry);
                            continue;
                        }

                        var item = libraryManager.FindByPath(newPath, false);
                        if (item is null || item.Id == entry.OldItemId)
                        {
                            continue;
                        }

                        var playlist = playlistManager.GetPlaylists(entry.OwnerId).FirstOrDefault(p => p.Id == entry.PlaylistId);
                        if (playlist is not null)
                        {
                            // Jellyfin retire parfois lui-même de la liste l'ancien élément dès qu'il disparaît de la médiathèque : on garde alors la place notée au départ.
                            var current = playlist.LinkedChildren;
                            var index = Array.FindIndex(current, c => c.ItemId == entry.OldItemId);
                            var oldStillThere = index >= 0;
                            var position = oldStillThere ? index : Math.Min(entry.Index, current.Length);
                            await playlistManager.AddItemToPlaylistAsync(entry.PlaylistId, new[] { item.Id }, position, entry.OwnerId).ConfigureAwait(false);
                            if (oldStillThere)
                            {
                                await playlistManager.RemoveItemFromPlaylistAsync(entry.PlaylistId.ToString("N"), new[] { entry.OldItemId.ToString("N") }).ConfigureAwait(false);
                            }

                            logger.LogInformation("MediaUploader: liste de lecture « {Name} » : {Path} remis en position {Position}", playlist.Name, newPath, position);
                        }
                        else
                        {
                            logger.LogInformation("MediaUploader: liste de lecture {Id} introuvable : {Path} non remis", entry.PlaylistId, newPath);
                        }

                        pending.Remove(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MediaUploader: réparation des listes de lecture impossible");
            }
        });

        return entries.Count;
    }
}
