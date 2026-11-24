using Jellyfin.Plugin.MediaUploader.Services.Import;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Un dossier créé par l'import d'une liste de lecture (« Various Artists/Nom de la liste/52 - Titre.m4a »).</summary>
/// <param name="Folder">Dossier (relatif à la bibliothèque).</param>
/// <param name="Playlist">Nom de la liste de lecture Jellyfin du même nom.</param>
/// <param name="Files">Nombre de morceaux.</param>
public sealed record PlaylistFolder(string Folder, string Playlist, int Files);

/// <summary>Ce qui est prévu pour un morceau d'un dossier de liste de lecture.</summary>
public sealed class PlaylistFolderRow
{
    /// <summary>Gets or sets le fichier (relatif).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets le titre.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets la mention d'artistes actuelle.</summary>
    public string? Credit { get; set; }

    /// <summary>Gets or sets l'action : "move" (ranger dans Artiste/Album), "keep" (garder la copie déjà présente, celle-ci va à la corbeille) ou "leave".</summary>
    public string Action { get; set; } = "move";

    /// <summary>Gets or sets les copies du même morceau déjà rangées ailleurs (relatives), la conseillée en premier.</summary>
    public List<string> Copies { get; set; } = new();

    /// <summary>Gets or sets les artistes (« A; B »).</summary>
    public string Artists { get; set; } = string.Empty;

    /// <summary>Gets or sets l'artiste de l'album (dossier).</summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>Gets or sets l'album.</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>Gets or sets le numéro de piste (0 : aucun).</summary>
    public int Track { get; set; }

    /// <summary>Gets or sets le numéro de disque (0 : aucun).</summary>
    public int Disc { get; set; }

    /// <summary>Gets or sets l'année (0 : inconnue).</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets l'origine des valeurs : "musicbrainz", "rules" (MusicBrainz sans réponse ou désactivé) ou "pending" (vérification en cours).</summary>
    public string Source { get; set; } = "rules";
}

/// <summary>
/// Dossiers créés par l'import d'une liste de lecture en « tout dans un album » : chaque morceau y est rangé sous « Various Artists »,
/// numéroté d'après sa place dans la liste, et double souvent un morceau déjà présent. Les répartir : un morceau déjà présent ailleurs
/// est retiré au profit de cette copie, les autres sont rangés dans « Artiste/Album » (album trouvé sur MusicBrainz) ; la liste de lecture
/// garde ses morceaux, dans le même ordre.
/// </summary>
public static class PlaylistFolders
{
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".alac", ".aiff", ".aif", ".ape", ".wv", ".dsf" };

    /// <summary>
    /// Dossiers « Various Artists/X » dont le nom est celui d'une liste de lecture Jellyfin.
    /// </summary>
    /// <param name="rootPath">Dossier musique.</param>
    /// <param name="playlistNames">Noms des listes de lecture (de tous les utilisateurs).</param>
    /// <param name="isAudio">Vrai pour un fichier audio.</param>
    /// <returns>Dossiers.</returns>
    public static List<PlaylistFolder> Find(string rootPath, IEnumerable<string> playlistNames, Func<string, bool> isAudio)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in playlistNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            byKey.TryAdd(NameTools.TitleKey(PathBuilder.SanitizeSegment(name, name)), name);
        }

        var result = new List<PlaylistFolder>();
        var various = System.IO.Path.Combine(rootPath, ArtistCredits.VariousArtists);
        if (byKey.Count == 0 || !Directory.Exists(various))
        {
            return result;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(various).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!byKey.TryGetValue(NameTools.TitleKey(name), out var playlist))
                {
                    continue;
                }

                var files = Directory.EnumerateFiles(dir).Count(isAudio);
                if (files > 0)
                {
                    result.Add(new PlaylistFolder(ArtistCredits.VariousArtists + "/" + name, playlist, files));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dossier illisible : rien à signaler.
        }

        return result;
    }

    /// <summary>
    /// Ce qui est prévu pour chaque morceau du dossier.
    /// </summary>
    /// <param name="rootPath">Dossier musique.</param>
    /// <param name="files">Morceaux du dossier : chemin relatif, chemin complet, tags.</param>
    /// <param name="library">Morceaux connus de la bibliothèque (chemin complet et tags).</param>
    /// <param name="exists">Vrai si un fichier existe.</param>
    /// <param name="sizeOf">Taille d'un fichier.</param>
    /// <param name="lookup">Réponse de MusicBrainz déjà connue pour (titre, artistes) ; null si MusicBrainz est désactivé.</param>
    /// <param name="artistNames">Noms à virgule confirmés.</param>
    /// <param name="libraryArtists">Clés des artistes de la bibliothèque.</param>
    /// <returns>Un plan par morceau, dans l'ordre du dossier.</returns>
    public static List<PlaylistFolderRow> Plan(
        string rootPath,
        IReadOnlyList<(string Relative, string Full, TagSnapshot Tags)> files,
        IReadOnlyList<(string Path, TagInfo Tags)> library,
        Func<string, bool> exists,
        Func<string, long> sizeOf,
        Func<string, string, (bool Done, MbCredit? Credit)>? lookup,
        ICollection<string>? artistNames,
        ISet<string>? libraryArtists)
    {
        var root = rootPath.TrimEnd('/', '\\');
        var inFolder = files.Select(f => f.Full).ToHashSet(StringComparer.Ordinal);
        var others = library
            .Where(e => !inFolder.Contains(e.Path) && !e.Path.Contains(LibraryFs.TrashDirName, StringComparison.Ordinal) && e.Path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(e => (e.Path, Key: DuplicateMatcher.Key(NameTools.DedupeArtists(e.Tags.Artist), e.Tags.Title)))
            .Where(e => e.Key is not null)
            .ToList();

        var rows = new List<PlaylistFolderRow>();
        foreach (var (relative, full, tags) in files)
        {
            var title = string.IsNullOrWhiteSpace(tags.Title) ? System.IO.Path.GetFileNameWithoutExtension(full) : tags.Title!;
            var credit = NameTools.DedupeArtists(tags.Artist) ?? tags.Artist ?? string.Empty;
            var row = new PlaylistFolderRow { Path = relative, Title = title, Credit = tags.Artist, Year = (int)tags.Year };

            // Le même morceau déjà rangé ailleurs : cette copie-ci est retirée, la liste de lecture renverra vers l'autre.
            var key = DuplicateMatcher.Key(credit, title);
            if (key is not null)
            {
                row.Copies = others.Where(o => DuplicateMatcher.Same(key, o.Key!) && exists(o.Path))
                    .Select(o => o.Path)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(p => Lossless.Contains(System.IO.Path.GetExtension(p)))
                    .ThenBy(p => p.Contains(System.IO.Path.DirectorySeparatorChar + ArtistCredits.VariousArtists + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .ThenByDescending(sizeOf)
                    .Select(p => p[(root.Length + 1)..].Replace('\\', '/'))
                    .ToList();
            }

            if (row.Copies.Count > 0)
            {
                row.Action = "keep";
            }

            // Où ranger le morceau : MusicBrainz (album d'origine, piste), sinon un single « Artiste/Titre ».
            var split = ArtistCredits.Split(credit.Length > 0 ? credit : "Unknown Artist", artistNames, libraryArtists);
            var (done, mb) = lookup?.Invoke(title, credit) ?? (true, null);
            if (mb is not null)
            {
                var known = mb.Artists.Concat(mb.OriginalNames).Select(ArtistCredits.Key).ToHashSet(StringComparer.Ordinal);
                var extra = split.Artists.Where(a => !known.Contains(ArtistCredits.Key(a)) && !known.Any(k => k.Length > 3 && ArtistCredits.Key(a).Contains(k, StringComparison.Ordinal)));
                var artists = mb.Artists.Concat(extra).ToList();
                row.Artists = string.Join("; ", artists);
                row.AlbumArtist = mb.FolderArtist;
                row.Album = mb.Release ?? title;
                row.Track = mb.Release is null ? 1 : mb.Track;
                row.Disc = mb.Disc > 1 ? mb.Disc : 0;
                row.Year = mb.Year > 0 ? mb.Year : row.Year;
                row.Source = "musicbrainz";
            }
            else
            {
                row.Artists = string.Join("; ", split.Artists);
                row.AlbumArtist = ArtistCredits.FolderArtist(split.Artists);
                row.Album = title;
                row.Track = 1;
                row.Source = done ? "rules" : "pending";
            }

            rows.Add(row);
        }

        return rows;
    }
}
