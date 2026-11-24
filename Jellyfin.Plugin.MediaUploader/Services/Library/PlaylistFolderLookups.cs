using System.Collections.Concurrent;
using Jellyfin.Plugin.MediaUploader.Services.Import;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Vérification en arrière-plan, sur MusicBrainz, des morceaux d'un dossier de liste de lecture (album d'origine, piste, artistes).
/// </summary>
public static class PlaylistFolderLookups
{
    private static readonly ConcurrentDictionary<string, Task> Running = new(StringComparer.Ordinal);

    /// <summary>
    /// Lance (si besoin) la vérification sur MusicBrainz des morceaux d'un dossier, une requête par seconde, en arrière-plan.
    /// </summary>
    /// <param name="key">Identifiant du dossier.</param>
    /// <param name="songs">Titre et mention d'artistes de chaque morceau pas encore vérifié.</param>
    /// <param name="logger">Logger.</param>
    public static void Ensure(string key, IReadOnlyList<(string Title, string Credit)> songs, ILogger logger)
    {
        if (songs.Count == 0 || Running.ContainsKey(key))
        {
            return;
        }

        Running[key] = Task.Run(async () =>
        {
            var failures = 0;
            try
            {
                foreach (var (title, credit) in songs)
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await MusicBrainzCredits.LookupAsync(title, credit, null, CancellationToken.None).ConfigureAwait(false);
                            failures = 0;
                            break;
                        }
                        catch (ImportException) when (attempt < 2)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1))).ConfigureAwait(false);
                        }
                        catch (ImportException ex) when (++failures < 3)
                        {
                            logger.LogDebug("MediaUploader: MusicBrainz sans réponse pour {Title} : {Message}", title, ex.Message);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MediaUploader: vérification sur MusicBrainz interrompue");
            }
            finally
            {
                Running.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// Vrai si une vérification est en cours pour ce dossier.
    /// </summary>
    /// <param name="key">Identifiant du dossier.</param>
    /// <returns>Résultat.</returns>
    public static bool IsRunning(string key) => Running.ContainsKey(key);
}
