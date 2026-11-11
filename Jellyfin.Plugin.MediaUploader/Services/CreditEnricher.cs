using System.Collections.Concurrent;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services.Import;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>État de la vérification des artistes d'un lot sur MusicBrainz.</summary>
/// <param name="Pending">Morceaux encore à vérifier.</param>
/// <param name="Error">Raison d'un arrêt (MusicBrainz injoignable…) : les règles locales s'appliquent alors.</param>
public sealed record CreditCheck(int Pending, string? Error);

/// <summary>
/// Vérifie en arrière-plan, sur MusicBrainz, les morceaux d'un lot dont la mention d'artistes est ambiguë (« A, B », « A feat. B »)
/// ou en écriture non latine. Une requête par seconde : l'aperçu se complète au fur et à mesure, sans jamais bloquer l'envoi
/// (un morceau pas encore vérifié, introuvable ou sans correspondance sûre suit les règles locales).
/// </summary>
public static class CreditEnricher
{
    private static readonly ConcurrentDictionary<string, Task> Running = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Lance la vérification des morceaux du lot qui en ont besoin (si elle ne tourne pas déjà) et en donne l'état.
    /// </summary>
    /// <param name="batch">Lot.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Journal.</param>
    /// <returns>État.</returns>
    public static CreditCheck Ensure(Batch batch, PluginConfiguration config, ILogger logger)
    {
        if (!config.MusicBrainzArtists)
        {
            return new CreditCheck(0, null);
        }

        var todo = Todo(batch, config);
        if (todo.Count == 0 || batch.CreditError is not null)
        {
            return new CreditCheck(0, batch.CreditError);
        }

        lock (Gate)
        {
            if (!Running.ContainsKey(batch.Id))
            {
                Running[batch.Id] = Task.Run(() => RunAsync(batch, config, logger));
            }
        }

        return new CreditCheck(todo.Count, null);
    }

    private static List<(string Title, string Credit, string? Album)> Todo(Batch batch, PluginConfiguration config)
    {
        var audio = PlanFactory.ParseExtensions(config.AudioExtensions);
        lock (batch.Sync)
        {
            var songs = batch.Items
                .Where(i => i.Complete && audio.Contains(i.Ext) && !string.IsNullOrWhiteSpace(i.Tags?.Title))
                .Select(i => (Title: i.Tags!.Title!, Credit: i.Tags.Performers ?? i.Tags.Artist, i.Tags.Album))
                .Where(t => !string.IsNullOrWhiteSpace(t.Credit))
                .ToList();

            // Un album dont un morceau est ambigu est vérifié en entier : tous ses morceaux auront le même artiste d'album (Encanto : les compositeurs).
            var ambiguous = songs.Where(t => ArtistCredits.IsMultiple(t.Credit) || ArtistCredits.HasNonLatin(t.Credit)).ToList();
            var albums = ambiguous.Select(t => NameTools.TitleKey(t.Album)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
            return songs
                .Where(t => ArtistCredits.IsMultiple(t.Credit) || ArtistCredits.HasNonLatin(t.Credit) || albums.Contains(NameTools.TitleKey(t.Album)))
                .Where(t => !MusicBrainzCredits.TryGetCached(t.Title, t.Credit, t.Album, out _))
                .GroupBy(t => MusicBrainzCredits.CacheKey(t.Title, t.Credit, t.Album))
                .Select(g => (g.First().Title, g.First().Credit!, g.First().Album))
                .ToList();
        }
    }

    private static async Task RunAsync(Batch batch, PluginConfiguration config, ILogger logger)
    {
        try
        {
            var failures = 0;

            // Plusieurs passes : des morceaux peuvent finir d'arriver pendant la vérification (un morceau sans réponse est retenté à la passe suivante).
            for (var pass = 0; pass < 20; pass++)
            {
                var todo = Todo(batch, config);
                if (todo.Count == 0)
                {
                    break;
                }

                var done = 0;
                foreach (var (title, credit, album) in todo)
                {
                    if (!UploadStore.Batches.ContainsKey(batch.Id))
                    {
                        return;
                    }

                    // MusicBrainz répond souvent « 503 » quand il est chargé : on patiente et on réessaie ; on n'abandonne (règles locales)
                    // qu'après plusieurs morceaux de suite sans réponse.
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await MusicBrainzCredits.LookupAsync(title, credit, album, CancellationToken.None).ConfigureAwait(false);
                            failures = 0;
                            break;
                        }
                        catch (ImportException) when (attempt < 3)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1) * (attempt + 1))).ConfigureAwait(false);
                        }
                        catch (ImportException) when (++failures < 3)
                        {
                            break;
                        }
                    }

                    // Le plan est recalculé par paquets (l'aperçu se met à jour sans recalculer à chaque requête).
                    if (++done % 5 == 0)
                    {
                        batch.Invalidate();
                    }
                }

                batch.Invalidate();
            }
        }
        catch (ImportException ex)
        {
            batch.CreditError = ex.Message;
            logger.LogWarning("MediaUploader: vérification des artistes sur MusicBrainz interrompue : {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            batch.CreditError = "Vérification sur MusicBrainz interrompue.";
            logger.LogWarning(ex, "MediaUploader: vérification des artistes sur MusicBrainz interrompue");
        }
        finally
        {
            Running.TryRemove(batch.Id, out _);
            batch.Invalidate();
        }
    }
}
