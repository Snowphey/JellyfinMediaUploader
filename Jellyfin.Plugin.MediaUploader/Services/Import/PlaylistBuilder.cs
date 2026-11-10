using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Audio;
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
    /// <param name="m3u">Contenu d'un fichier .m3u : une liste existante est alors entièrement réordonnée d'après lui (null : les morceaux sont ajoutés à la fin).</param>
    public static void Queue(ILibraryManager libraryManager, IPlaylistManager playlistManager, PlaylistRequest spec, IReadOnlyList<string> paths, ILogger logger, string? m3u = null)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(libraryManager, playlistManager, spec, paths, logger, m3u).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaUploader: liste de lecture « {Name} » non créée", spec.Name);
            }
        });
    }

    private static async Task RunAsync(ILibraryManager libraryManager, IPlaylistManager playlistManager, PlaylistRequest spec, IReadOnlyList<string> paths, ILogger logger, string? m3u)
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
                    // En tête si demandé (le .m3u, lui, replace ensuite toute la liste) ; sinon à la fin.
                    var atTop = spec.AtTop && string.IsNullOrWhiteSpace(m3u);
                    await playlistManager.AddItemToPlaylistAsync(existing.Id, toAdd, atTop ? 0 : null, spec.UserId).ConfigureAwait(false);
                }

                logger.LogInformation("MediaUploader: liste de lecture « {Name} » complétée ({Count} morceaux ajoutés {Where})", spec.Name, toAdd.Count, spec.AtTop && string.IsNullOrWhiteSpace(m3u) ? "en tête" : "à la fin");

                // Un .m3u fourni pour une liste existante : toute la liste suit son ordre (morceaux déjà présents compris) ;
                // ceux que le fichier ne cite pas gardent leur ordre, à la fin. Rien n'est retiré.
                if (!string.IsNullOrWhiteSpace(m3u))
                {
                    await ReorderAsync(libraryManager, playlistManager, existing.Id, spec, m3u, logger).ConfigureAwait(false);
                }
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

    private static async Task ReorderAsync(ILibraryManager libraryManager, IPlaylistManager playlistManager, Guid playlistId, PlaylistRequest spec, string m3u, ILogger logger)
    {
        if (libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return;
        }

        // Entrées de la liste, dans l'ordre actuel (identifiant « N », comme l'attend MoveItemAsync).
        var current = playlist.LinkedChildren.Where(c => c.ItemId.HasValue).Select(c => c.ItemId!.Value.ToString("N")).ToList();
        var candidates = new List<M3uCandidate>();
        foreach (var id in current.Distinct(StringComparer.Ordinal))
        {
            var item = libraryManager.GetItemById(Guid.ParseExact(id, "N"));
            if (item is not null)
            {
                var artist = item is Audio audio ? string.Join(", ", audio.Artists) : null;
                candidates.Add(new M3uCandidate(id, item.Path ?? string.Empty, artist, item.Name));
            }
        }

        var matched = M3uOrder.Match(m3u, candidates).Order;
        var target = matched.Concat(current.Where(id => !matched.Contains(id, StringComparer.Ordinal))).ToList();
        var moves = 0;
        for (var i = 0; i < target.Count && i < current.Count; i++)
        {
            if (string.Equals(current[i], target[i], StringComparison.Ordinal))
            {
                continue;
            }

            await playlistManager.MoveItemAsync(playlistId.ToString("N"), target[i], i, spec.UserId).ConfigureAwait(false);
            current.Remove(target[i]);
            current.Insert(i, target[i]);
            moves++;
        }

        logger.LogInformation("MediaUploader: liste de lecture « {Name} » réordonnée d'après le .m3u ({Matched} morceaux retrouvés, {Moves} déplacés)", spec.Name, matched.Count, moves);
    }
}
