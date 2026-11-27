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
    private static List<(string Path, IReadOnlyList<string> Artists)> _artistLists = new();
    private static List<(string Path, IReadOnlyList<string> Artists)> _albumArtistLists = new();
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

    /// <summary>
    /// Clés des artistes présents dans un dossier musique : artistes des morceaux connus de Jellyfin et dossiers d'artiste.
    /// </summary>
    /// <param name="root">Dossier musique.</param>
    /// <returns>Clés (<see cref="ArtistCredits.KnownKeys"/>).</returns>
    public static HashSet<string> ArtistKeys(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        IEnumerable<string?> dirs;
        try
        {
            dirs = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).ToList() : new List<string?>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dirs = new List<string?>();
        }

        // Un nom « A & B » ne compte comme artiste à part entière que s'il a son dossier : une fiche de Jellyfin « Theodora & Jul »
        // vient justement d'une mention à séparer.
        var keys = new HashSet<string>(ArtistCredits.KnownKeys(Under(root).Select(e => e.Tags.Artist)).Where(k => !k.StartsWith(ArtistCredits.StandalonePrefix, StringComparison.Ordinal)), StringComparer.Ordinal);
        keys.UnionWith(ArtistCredits.KnownKeys(dirs));
        return keys;
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
            var artistLists = new List<(string Path, IReadOnlyList<string> Artists)>();
            var albumArtistLists = new List<(string Path, IReadOnlyList<string> Artists)>();
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

                        if (item.Artists is { Count: > 0 } raw)
                        {
                            artistLists.Add((item.Path, raw.ToList()));
                        }

                        if (item.AlbumArtists is { Count: > 0 } rawAlbum)
                        {
                            albumArtistLists.Add((item.Path, rawAlbum.ToList()));
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
            _artistLists = artistLists;
            _albumArtistLists = albumArtistLists;
            _at = DateTime.UtcNow;
            return list;
        }
    }

    /// <summary>
    /// Artistes de chaque morceau tels que Jellyfin les a lus (un par fiche d'artiste), sous un dossier.
    /// </summary>
    /// <param name="root">Dossier musique.</param>
    /// <returns>Chemin et artistes.</returns>
    /// <param name="albumArtists">Vrai : artistes de l'album (la page « Artistes » de Jellyfin les montre aussi).</param>
    public static IReadOnlyList<(string Path, IReadOnlyList<string> Artists)> ArtistLists(string root, bool albumArtists = false)
    {
        Snapshot();
        lock (Gate)
        {
            var source = albumArtists ? _albumArtistLists : _artistLists;
            return string.IsNullOrWhiteSpace(root) ? Array.Empty<(string, IReadOnlyList<string>)>() : source.Where(e => e.Path.StartsWith(root, StringComparison.Ordinal)).ToList();
        }
    }
}
