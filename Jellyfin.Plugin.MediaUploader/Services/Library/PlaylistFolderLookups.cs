using System.Collections.Concurrent;
using Jellyfin.Plugin.MediaUploader.Services.Import;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Vérification en arrière-plan, sur MusicBrainz, des morceaux d'un dossier de liste de lecture (album d'origine, piste, artistes).
/// </summary>
public static class PlaylistFolderLookups
{
    private const int Rounds = 4;
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
            try
            {
                // MusicBrainz est souvent surchargé : un morceau sans réponse est repris au tour suivant (4 tours, espacés de 15, 30 puis 45 s),
                // et une série d'échecs ralentit la cadence au lieu d'arrêter la vérification.
                var left = songs.ToList();
                for (var round = 0; round < Rounds && left.Count > 0; round++)
                {
                    if (round > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15 * round)).ConfigureAwait(false);
                    }

                    var failed = new List<(string Title, string Credit)>();
                    var streak = 0;
                    foreach (var song in left)
                    {
                        try
                        {
                            await MusicBrainzCredits.LookupAsync(song.Title, song.Credit, null, CancellationToken.None).ConfigureAwait(false);
                            streak = 0;
                        }
                        catch (ImportException ex)
                        {
                            failed.Add(song);
                            streak++;
                            logger.LogDebug("MediaUploader: MusicBrainz sans réponse pour {Title} (tour {Round}) : {Message}", song.Title, round + 1, ex.Message);
                            await Task.Delay(TimeSpan.FromSeconds(3 * Math.Min(streak, 5))).ConfigureAwait(false);
                        }
                    }

                    left = failed;
                }

                if (left.Count > 0)
                {
                    logger.LogInformation("MediaUploader: MusicBrainz sans réponse pour {Count} morceau(x) après {Rounds} tours", left.Count, Rounds);
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
