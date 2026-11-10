using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Éléments à regrouper : un artiste réparti sur plusieurs dossiers, ou un album réparti entre plusieurs artistes.</summary>
/// <param name="Name">Nom commun (artiste principal, ou titre de l'album).</param>
/// <param name="Items">Dossiers concernés (chemins relatifs à la racine).</param>
/// <param name="Artist">Artiste de l'album proposé pour le regroupement.</param>
/// <param name="Album">Nom d'album proposé (albums seulement), ou null.</param>
public sealed record ScatteredGroup(string Name, IReadOnlyList<string> Items, string Artist, string? Album);

/// <summary>Une copie d'un morceau présent plusieurs fois.</summary>
/// <param name="Path">Chemin relatif à la racine.</param>
/// <param name="Size">Taille en octets.</param>
public sealed record DuplicateSongCopy(string Path, long Size);

/// <summary>Un morceau présent plusieurs fois dans la bibliothèque.</summary>
/// <param name="Label">« Artiste — Titre ».</param>
/// <param name="Copies">Copies.</param>
/// <param name="Keep">Indice de la copie conseillée (sans perte, sinon la plus grosse).</param>
public sealed record DuplicateSongGroup(string Label, IReadOnlyList<DuplicateSongCopy> Copies, int Keep);

/// <summary>Résultat des contrôles de cohérence de la bibliothèque musique.</summary>
/// <param name="ScatteredArtists">Artistes répartis sur plusieurs dossiers (« K DA, X » et « K DA, Y », « Orelsan » et « OrelSan »).</param>
/// <param name="ScatteredAlbums">Albums répartis entre plusieurs dossiers d'artiste.</param>
/// <param name="DuplicateSongs">Morceaux présents plusieurs fois.</param>
/// <param name="Truncated">Vrai si une limite a été atteinte.</param>
public sealed record MusicHealthReport(IReadOnlyList<ScatteredGroup> ScatteredArtists, IReadOnlyList<ScatteredGroup> ScatteredAlbums, IReadOnlyList<DuplicateSongGroup> DuplicateSongs, bool Truncated);

/// <summary>
/// Contrôles de cohérence de la bibliothèque musique : dossiers d'artiste et d'album à regrouper, morceaux en double.
/// Rien n'est modifié ici : les corrections passent par l'éditeur de tags (rangement) ou la résolution des doublons, qui gardent les listes de lecture.
/// </summary>
public static class MusicHealth
{
    /// <summary>Nom du dossier des compilations.</summary>
    public const string VariousArtists = "Various Artists";

    private const int MaxGroups = 200;
    private const long MaxComparisons = 3_000_000;
    private static readonly string[] LeadSeparators = { ",", ";", " feat.", " feat ", " ft.", " ft ", " featuring ", " with " };
    private static readonly Regex LeadingThe = new(@"^(the|les?|la)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".alac", ".aiff", ".aif", ".ape", ".wv", ".dsf" };

    /// <summary>
    /// Artiste principal d'une mention : « K DA, Madison Beer » donne « K DA », « Freeze Corleone feat. X » donne « Freeze Corleone ».
    /// « & » n'est pas un séparateur (« Bigflo & Oli », « Simon & Garfunkel »).
    /// </summary>
    /// <param name="credit">Mention (nom de dossier ou tag).</param>
    /// <returns>Artiste principal.</returns>
    public static string Lead(string credit)
    {
        var text = credit.Trim();
        var cut = text.Length;
        foreach (var sep in LeadSeparators)
        {
            var i = text.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i < cut)
            {
                cut = i;
            }
        }

        return text[..cut].Trim();
    }

    /// <summary>
    /// Clé de comparaison d'un artiste : casse, accents, ponctuation et article en tête ignorés (« K/DA » et « K DA », « The Weeknd » et « Weeknd »).
    /// </summary>
    /// <param name="artist">Artiste.</param>
    /// <returns>Clé.</returns>
    public static string ArtistKey(string artist) => NameTools.TitleKey(LeadingThe.Replace(artist.Trim(), string.Empty));

    /// <summary>
    /// Analyse la bibliothèque musique.
    /// </summary>
    /// <param name="rootPath">Dossier racine.</param>
    /// <param name="known">Morceaux connus de Jellyfin (chemin complet et tags), pour les doublons.</param>
    /// <param name="exists">Vrai si un fichier existe encore (l'index de Jellyfin peut être en retard).</param>
    /// <param name="sizeOf">Taille d'un fichier.</param>
    /// <returns>Rapport.</returns>
    public static MusicHealthReport Analyze(string rootPath, IEnumerable<(string Path, TagInfo Tags)> known, Func<string, bool> exists, Func<string, long> sizeOf)
    {
        var truncated = false;
        var artistDirs = new List<string>();
        var albumDirs = new List<(string Artist, string Album)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.')).Cast<string>())
            {
                artistDirs.Add(dir);
                try
                {
                    foreach (var album in Directory.EnumerateDirectories(Path.Combine(rootPath, dir)).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.')).Cast<string>())
                    {
                        albumDirs.Add((dir, album));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Dossier illisible : ignoré.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MusicHealthReport(Array.Empty<ScatteredGroup>(), Array.Empty<ScatteredGroup>(), Array.Empty<DuplicateSongGroup>(), true);
        }

        var knownList = known as IReadOnlyCollection<(string Path, TagInfo Tags)> ?? known.ToList();
        var artists = ScatteredArtists(rootPath, artistDirs, albumDirs, knownList, ref truncated);
        var albums = ScatteredAlbums(albumDirs, ref truncated);
        var songs = DuplicateSongs(rootPath, knownList, exists, sizeOf, ref truncated);
        return new MusicHealthReport(artists, albums, songs, truncated);
    }

    private static List<ScatteredGroup> ScatteredArtists(string rootPath, List<string> artistDirs, List<(string Artist, string Album)> albumDirs, IReadOnlyCollection<(string Path, TagInfo Tags)> known, ref bool truncated)
    {
        // Artiste principal tel qu'écrit dans les tags (« K/DA »), par dossier d'artiste : un nom de dossier a perdu certains caractères (« K DA »).
        var root = rootPath.TrimEnd('/', '\\');
        var tagLeads = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (path, tags) in known)
        {
            if (string.IsNullOrWhiteSpace(tags.Artist) || path.Length <= root.Length + 1 || !path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var rel = path[(root.Length + 1)..];
            var cut = rel.IndexOfAny(new[] { '/', '\\' });
            if (cut <= 0)
            {
                continue;
            }

            var dir = rel[..cut];
            if (!tagLeads.TryGetValue(dir, out var counts))
            {
                tagLeads[dir] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            var lead = Lead(NameTools.DedupeArtists(tags.Artist) ?? tags.Artist);
            counts[lead] = counts.GetValueOrDefault(lead) + 1;
        }

        var result = new List<ScatteredGroup>();
        var albumCount = albumDirs.GroupBy(a => a.Artist, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var g in artistDirs.GroupBy(d => ArtistKey(Lead(d))).Where(g => g.Key.Length >= 2 && g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (result.Count >= MaxGroups)
            {
                truncated = true;
                break;
            }

            // Nom proposé : un dossier qui ne porte que l'artiste principal (le plus fourni s'il y en a plusieurs, « Orelsan » / « OrelSan »), sinon l'artiste principal.
            var folders = g.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            var plain = folders.Where(d => string.Equals(Lead(d), d.Trim(), StringComparison.Ordinal))
                .OrderByDescending(d => albumCount.GetValueOrDefault(d))
                .FirstOrDefault();
            var name = plain ?? Lead(folders.OrderByDescending(d => albumCount.GetValueOrDefault(d)).First());
            var spelled = folders.SelectMany(f => tagLeads.GetValueOrDefault(f) ?? new Dictionary<string, int>())
                .Where(kv => ArtistKey(kv.Key) == g.Key)
                .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .OrderByDescending(x => x.Sum(kv => kv.Value))
                .Select(x => x.Key)
                .FirstOrDefault();
            name = spelled ?? name;
            result.Add(new ScatteredGroup(name, folders, name, null));
        }

        return result;
    }

    private static List<ScatteredGroup> ScatteredAlbums(List<(string Artist, string Album)> albumDirs, ref bool truncated)
    {
        var result = new List<ScatteredGroup>();
        foreach (var g in albumDirs.GroupBy(a => LibraryFs.AlbumKey(a.Album)).Where(g => g.Key.Length >= 3).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var leads = g.Select(a => ArtistKey(Lead(a.Artist))).Distinct(StringComparer.Ordinal).ToList();

            // Même artiste principal partout : c'est un artiste réparti, signalé à part.
            if (leads.Count < 2)
            {
                continue;
            }

            // Garde-fou contre les titres courants (« Greatest Hits », « Live ») d'artistes sans rapport : un artiste en commun, une compilation,
            // ou un titre assez long pour être distinctif (bandes originales…).
            var various = g.Any(a => ArtistKey(a.Artist) == ArtistKey(VariousArtists));
            var tokens = g.Select(a => Word.Matches(a.Artist).Select(m => NameTools.TitleKey(m.Value)).Where(t => t.Length >= 3).ToHashSet(StringComparer.Ordinal)).ToList();
            var shareArtist = tokens.Skip(1).Any(t => t.Overlaps(tokens[0]));
            if (!various && !shareArtist && g.Key.Length < 15)
            {
                continue;
            }

            if (result.Count >= MaxGroups)
            {
                truncated = true;
                break;
            }

            // Artiste proposé : celui commun à tous les dossiers (« Lorien Testard »), sinon « Various Artists ».
            string[] Credits(string a) => a.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var common = g.Select(a => Credits(a.Artist).Select(ArtistKey).ToHashSet(StringComparer.Ordinal))
                .Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));

            // Le nom tel qu'écrit dans un des dossiers.
            var artist = common.Count > 0 ? g.SelectMany(a => Credits(a.Artist)).First(p => common.Contains(ArtistKey(p))) : VariousArtists;

            var items = g.Select(a => a.Artist + "/" + a.Album).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var album = g.GroupBy(a => a.Album, StringComparer.Ordinal).OrderByDescending(x => x.Count()).ThenBy(x => x.Key.Length).First().Key;
            result.Add(new ScatteredGroup(album, items, artist, album));
        }

        return result;
    }

    private static List<DuplicateSongGroup> DuplicateSongs(string rootPath, IEnumerable<(string Path, TagInfo Tags)> known, Func<string, bool> exists, Func<string, long> sizeOf, ref bool truncated)
    {
        var root = rootPath.TrimEnd('/', '\\');
        var songs = new List<(string Path, SongKey Key, string Label)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, tags) in known)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || path.Contains(LibraryFs.TrashDirName, StringComparison.Ordinal) || !seenPaths.Add(path))
            {
                continue;
            }

            var key = DuplicateMatcher.Key(NameTools.DedupeArtists(tags.Artist), tags.Title);
            if (key is not null)
            {
                songs.Add((path, key, $"{tags.Artist} — {tags.Title}"));
            }
        }

        // Union des paires « même morceau », comparées seulement entre morceaux qui partagent un artiste.
        var parent = Enumerable.Range(0, songs.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < songs.Count; i++)
        {
            foreach (var a in songs[i].Key.Artists.DefaultIfEmpty(songs[i].Key.ArtistFull))
            {
                if (!buckets.TryGetValue(a, out var list))
                {
                    buckets[a] = list = new List<int>();
                }

                list.Add(i);
            }
        }

        long comparisons = 0;
        foreach (var list in buckets.Values)
        {
            for (var x = 0; x < list.Count; x++)
            {
                for (var y = x + 1; y < list.Count; y++)
                {
                    if (++comparisons > MaxComparisons)
                    {
                        truncated = true;
                        goto Done;
                    }

                    var (i, j) = (list[x], list[y]);
                    if (Find(i) != Find(j) && DuplicateMatcher.SameTitle(songs[i].Key, songs[j].Key))
                    {
                        parent[Find(i)] = Find(j);
                    }
                }
            }
        }

    Done:
        var result = new List<DuplicateSongGroup>();
        foreach (var g in Enumerable.Range(0, songs.Count).GroupBy(Find).Where(g => g.Count() > 1))
        {
            var copies = g.Select(i => songs[i].Path).Where(exists).ToList();
            if (copies.Count < 2)
            {
                continue;
            }

            if (result.Count >= MaxGroups)
            {
                truncated = true;
                break;
            }

            var withSize = copies.Select(p => new DuplicateSongCopy(p[(root.Length + 1)..].Replace('\\', '/'), sizeOf(p))).OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var keep = withSize.Select((c, i) => (c, i))
                .OrderByDescending(t => Lossless.Contains(Path.GetExtension(t.c.Path)))
                .ThenByDescending(t => t.c.Size)
                .First().i;
            result.Add(new DuplicateSongGroup(songs[g.First()].Label, withSize, keep));
        }

        return result.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
