using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Morceaux connus de Jellyfin (artistes et titres de sa base), pour chercher les doublons dans toute la bibliothèque musique
/// sans relire les tags de chaque fichier. Gardé en mémoire deux minutes.
/// </summary>
public static class LibraryMusicIndex
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static ILibraryManager? _libraryManager;
    private static ILogger? _logger;
    private static List<(string Path, TagInfo Tags)>? _all;
    private static DateTime _at;

    /// <summary>
    /// Branche l'index sur la bibliothèque de Jellyfin (appelé par les contrôleurs).
    /// </summary>
    /// <param name="libraryManager">Bibliothèque.</param>
    /// <param name="logger">Logger.</param>
    public static void Attach(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger ??= logger;
    }

    /// <summary>
    /// Oublie l'index (après un envoi : les nouveaux morceaux y entreront au prochain calcul).
    /// </summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _all = null;
        }
    }

    /// <summary>
    /// Morceaux connus situés dans un dossier.
    /// </summary>
    /// <param name="root">Dossier musique.</param>
    /// <returns>Chemins complets et tags.</returns>
    public static IReadOnlyList<(string Path, TagInfo Tags)> Under(string root)
    {
        var all = Snapshot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return Array.Empty<(string, TagInfo)>();
        }

        return all.Where(e => e.Path.StartsWith(root, StringComparison.Ordinal)).ToList();
    }

    private static List<(string Path, TagInfo Tags)> Snapshot()
    {
        lock (Gate)
        {
            if (_all is not null && DateTime.UtcNow - _at < Ttl)
            {
                return _all;
            }

            var list = new List<(string, TagInfo)>();
            var lm = _libraryManager;
            if (lm is not null)
            {
                try
                {
                    var items = lm.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Audio },
                        Recursive = true,
                        IsVirtualItem = false
                    });
                    foreach (var item in items.OfType<AudioItem>())
                    {
                        if (string.IsNullOrEmpty(item.Path) || string.IsNullOrWhiteSpace(item.Name))
                        {
                            continue;
                        }

                        var artists = item.Artists is { Count: > 0 } a ? string.Join(", ", a) : item.AlbumArtists is { Count: > 0 } aa ? string.Join(", ", aa) : null;
                        if (artists is not null)
                        {
                            list.Add((item.Path, new TagInfo(artists, item.Album, item.Name)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Sans index, la recherche se limite au dossier de l'artiste (comportement d'origine).
                    _logger?.LogWarning(ex, "MediaUploader: index des morceaux de Jellyfin indisponible");
                }
            }

            _all = list;
            _at = DateTime.UtcNow;
            return list;
        }
    }
}
