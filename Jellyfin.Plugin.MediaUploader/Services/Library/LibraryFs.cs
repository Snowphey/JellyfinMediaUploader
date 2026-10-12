using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>
/// Gestion des fichiers des dossiers de bibliothèque : parcours, statistiques, déplacement, fusion, suppression avec corbeille, restauration, contrôle de santé.
/// Rien ne sort jamais d'un dossier racine : chaque chemin reçu est validé (pas de « .. », pas de caractères interdits, pas de lien symbolique pour une action destructrice),
/// et les éléments internes du plugin (préfixe « .mu- ») ne sont pas manipulables.
/// </summary>
public static class LibraryFs
{
    /// <summary>Nom du dossier de corbeille, à la racine de chaque bibliothèque.</summary>
    public const string TrashDirName = ".mu-corbeille";

    private static readonly Regex TrashId = new(@"^\d{14}-[0-9a-f]{6}$", RegexOptions.Compiled);
    private static readonly string[] CoverNames = { "cover", "folder", "poster", "front", "albumart" };
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class TrashMeta
    {
        public string Id { get; set; } = string.Empty;

        public string Root { get; set; } = string.Empty;

        public string OriginalPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool IsDir { get; set; }

        public int Files { get; set; }

        public long Bytes { get; set; }

        public DateTime DeletedAt { get; set; }

        public string DeletedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dossiers racine configurés et présents sur le disque.
    /// </summary>
    /// <param name="music">Dossier musique.</param>
    /// <param name="movies">Dossier films.</param>
    /// <param name="shows">Dossier séries.</param>
    /// <returns>Racines utilisables.</returns>
    public static List<LibraryRoot> Roots(string? music, string? movies, string? shows)
    {
        var roots = new List<LibraryRoot>();
        void Add(string key, string label, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path.Trim()))
            {
                roots.Add(new LibraryRoot(key, label, Path.GetFullPath(path.Trim())));
            }
        }

        Add("music", "Musique", music);
        Add("movies", "Films", movies);
        Add("shows", "Séries et animés", shows);
        return roots;
    }

    /// <summary>
    /// Valide un chemin relatif à un dossier racine et le convertit en chemin complet.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="relative">Chemin relatif (« Artiste/Album »), vide pour la racine.</param>
    /// <param name="full">Chemin complet.</param>
    /// <param name="error">Message en cas de refus.</param>
    /// <param name="allowInternal">Autorise les éléments internes du plugin (corbeille).</param>
    /// <returns>Vrai si le chemin est valide et reste dans la racine.</returns>
    public static bool TryResolve(LibraryRoot root, string? relative, out string full, out string? error, bool allowInternal = false)
    {
        full = root.Path;
        error = null;
        var rel = (relative ?? string.Empty).Replace('\\', '/').Trim('/');
        var parts = rel.Length == 0 ? Array.Empty<string>() : rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(invalid) >= 0)
            {
                error = "Chemin invalide.";
                return false;
            }

            if (!allowInternal && part.StartsWith(".mu-", StringComparison.Ordinal))
            {
                error = "Élément interne du plugin.";
                return false;
            }
        }

        full = Path.GetFullPath(Path.Combine(new[] { root.Path }.Concat(parts).ToArray()));
        if (!Same(full, root.Path) && !PathBuilder.IsInside(root.Path, full))
        {
            error = "Chemin hors de la bibliothèque.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Chemin relatif à la racine, avec des « / ».
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="full">Chemin complet.</param>
    /// <returns>Chemin relatif (vide pour la racine).</returns>
    public static string Relative(LibraryRoot root, string full)
    {
        var rel = Path.GetRelativePath(root.Path, full).Replace('\\', '/');
        return rel == "." ? string.Empty : rel;
    }

    /// <summary>
    /// Indique si le chemin, ou l'un de ses dossiers parents sous la racine, est un lien symbolique.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="full">Chemin complet.</param>
    /// <returns>Vrai s'il y a un lien.</returns>
    public static bool HasLink(LibraryRoot root, string full)
    {
        var current = root.Path;
        foreach (var part in Relative(root, full).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                return true;
            }

            // Un chemin qui n'existe pas encore (dossier à créer) ne peut pas contenir de lien.
            if (!info.Exists)
            {
                return false;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Liste le contenu d'un dossier (dossiers d'abord, puis fichiers, par nom).
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="relative">Dossier à lister.</param>
    /// <param name="kindOf">Type d'un fichier d'après son extension (avec le point, en minuscules).</param>
    /// <param name="entries">Éléments.</param>
    /// <param name="error">Message en cas de refus.</param>
    /// <returns>Vrai si le dossier a pu être listé.</returns>
    public static bool TryList(LibraryRoot root, string? relative, Func<string, string> kindOf, out List<LibraryEntry> entries, out string? error)
    {
        entries = new List<LibraryEntry>();
        if (!TryResolve(root, relative, out var full, out error))
        {
            return false;
        }

        if (!Directory.Exists(full))
        {
            error = "Dossier introuvable.";
            return false;
        }

        try
        {
            var atRoot = Same(full, root.Path);
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos().Take(5000))
            {
                if (atRoot && string.Equals(info.Name, TrashDirName, StringComparison.Ordinal))
                {
                    continue;
                }

                var hidden = info.Name.StartsWith('.');
                if (info is DirectoryInfo dir)
                {
                    var children = 0;
                    try
                    {
                        children = dir.EnumerateFileSystemInfos().Take(1000).Count();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        children = 0;
                    }

                    entries.Add(new LibraryEntry(dir.Name, true, 0, dir.LastWriteTimeUtc, children, "folder", hidden));
                }
                else if (info is FileInfo file)
                {
                    entries.Add(new LibraryEntry(file.Name, false, file.Length, file.LastWriteTimeUtc, 0, kindOf(file.Extension.ToLowerInvariant()), hidden));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Dossier illisible : " + ex.Message;
            return false;
        }

        entries = entries.OrderBy(e => e.IsDir ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    /// <summary>
    /// Compte les fichiers, dossiers et octets d'un élément.
    /// </summary>
    /// <param name="fullPath">Fichier ou dossier.</param>
    /// <param name="cap">Nombre maximal d'éléments parcourus.</param>
    /// <returns>Totaux.</returns>
    public static LibraryStats Stats(string fullPath, int cap = 200_000)
    {
        if (File.Exists(fullPath))
        {
            return new LibraryStats(1, 0, new FileInfo(fullPath).Length, false);
        }

        if (!Directory.Exists(fullPath))
        {
            return new LibraryStats(0, 0, 0, false);
        }

        int files = 0, dirs = 0, seen = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var info in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", options))
        {
            if (++seen > cap)
            {
                return new LibraryStats(files, dirs, bytes, true);
            }

            if (info is FileInfo file)
            {
                files++;
                bytes += file.Length;
            }
            else
            {
                dirs++;
            }
        }

        return new LibraryStats(files, dirs, bytes, false);
    }

    /// <summary>
    /// Liste les fichiers d'un élément (chemins relatifs à cet élément), pour montrer l'arborescence avant une suppression et détailler le journal d'activité.
    /// </summary>
    /// <param name="fullPath">Fichier ou dossier.</param>
    /// <param name="cap">Nombre maximal de fichiers listés.</param>
    /// <returns>Fichiers triés par chemin, et vrai si la liste est incomplète.</returns>
    public static (List<LibraryFile> Files, bool Truncated) Listing(string fullPath, int cap = 500)
    {
        var files = new List<LibraryFile>();
        if (File.Exists(fullPath))
        {
            files.Add(new LibraryFile(Path.GetFileName(fullPath), new FileInfo(fullPath).Length));
            return (files, false);
        }

        if (!Directory.Exists(fullPath))
        {
            return (files, false);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        var truncated = false;
        foreach (var info in new DirectoryInfo(fullPath).EnumerateFiles("*", options))
        {
            if (files.Count >= cap)
            {
                truncated = true;
                break;
            }

            files.Add(new LibraryFile(Path.GetRelativePath(fullPath, info.FullName).Replace('\\', '/'), info.Length));
        }

        return (files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), truncated);
    }

    /// <summary>
    /// Clé de comparaison de noms d'albums : sans casse, accents ni ponctuation. « X (Official Soundtrack) » et « X: Official Soundtrack » donnent la même clé.
    /// </summary>
    /// <param name="name">Nom de dossier ou d'album.</param>
    /// <returns>Clé (vide si le nom ne contient aucune lettre ni chiffre).</returns>
    public static string AlbumKey(string? name)
    {
        var decomposed = (name ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Fichiers audio contenus dans des éléments (un fichier audio lui-même, ou tous ceux d'un dossier, sous-dossiers compris).
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="relatives">Éléments (relatifs).</param>
    /// <param name="kindOf">Type d'un fichier d'après son extension.</param>
    /// <param name="cap">Nombre maximal de fichiers.</param>
    /// <param name="truncated">Vrai si la liste est incomplète.</param>
    /// <returns>Chemins relatifs des fichiers audio, triés.</returns>
    public static List<string> AudioFiles(LibraryRoot root, IEnumerable<string> relatives, Func<string, string> kindOf, int cap, out bool truncated)
    {
        truncated = false;
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var relative in relatives)
        {
            if (!TryResolve(root, relative, out var full, out _) || HasLink(root, full))
            {
                continue;
            }

            IEnumerable<string> files = File.Exists(full) ? new[] { full } : Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", options) : Array.Empty<string>();
            foreach (var file in files)
            {
                if (Path.GetFileName(file).StartsWith('.') || kindOf(Path.GetExtension(file).ToLowerInvariant()) != "audio")
                {
                    continue;
                }

                if (found.Count >= cap)
                {
                    truncated = true;
                    return found.ToList();
                }

                found.Add(Relative(root, file));
            }
        }

        return found.ToList();
    }

    /// <summary>
    /// Crée un dossier.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="parentRelative">Dossier parent (relatif).</param>
    /// <param name="name">Nom du nouveau dossier.</param>
    /// <param name="created">Chemin relatif du dossier créé.</param>
    /// <param name="error">Message en cas de refus.</param>
    /// <returns>Vrai si le dossier existe maintenant.</returns>
    public static bool TryCreateFolder(LibraryRoot root, string? parentRelative, string? name, out string created, out string? error)
    {
        created = string.Empty;
        if (!TryResolve(root, parentRelative, out var parentFull, out error))
        {
            return false;
        }

        var clean = PathBuilder.SanitizeSegment(name, string.Empty);
        if (clean.Length == 0 || clean.StartsWith(".mu-", StringComparison.Ordinal))
        {
            error = "Nom de dossier invalide.";
            return false;
        }

        if (HasLink(root, parentFull))
        {
            error = "Le dossier parent est un lien symbolique.";
            return false;
        }

        var full = Path.Combine(parentFull, clean);
        if (File.Exists(full))
        {
            error = "Un fichier porte déjà ce nom.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Création impossible : " + ex.Message;
            return false;
        }

        created = Relative(root, full);
        return true;
    }

    /// <summary>
    /// Déplace ou renomme des éléments d'un même dossier racine (ou en donne l'aperçu).
    /// Si la destination existe déjà et que les deux sont des dossiers, leur contenu est fusionné.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="sources">Éléments à déplacer (relatifs).</param>
    /// <param name="destRelative">Dossier de destination (relatif), créé si besoin.</param>
    /// <param name="newName">Nouveau nom (renommage d'un seul élément), sinon null.</param>
    /// <param name="policy">En cas de fichier déjà présent : "skip" (ignorer), "rename" (suffixe « (2) ») ou "overwrite" (écraser).</param>
    /// <param name="dryRun">Vrai pour un aperçu : rien n'est modifié.</param>
    /// <param name="isBusy">Indique qu'un chemin est en cours d'utilisation par un envoi ou un import.</param>
    /// <returns>Un résultat par élément.</returns>
    public static List<MoveResult> Move(LibraryRoot root, IReadOnlyList<string> sources, string? destRelative, string? newName, string policy, bool dryRun, Func<string, bool> isBusy)
    {
        var results = new List<MoveResult>();
        if (!TryResolve(root, destRelative, out var destFull, out var destError))
        {
            return sources.Select(s => Invalid(s, destError)).ToList();
        }

        if (newName is not null && sources.Count != 1)
        {
            return sources.Select(s => Invalid(s, "Un renommage ne concerne qu'un seul élément.")).ToList();
        }

        if (File.Exists(destFull) || HasLink(root, destFull))
        {
            return sources.Select(s => Invalid(s, "Destination invalide (fichier ou lien symbolique).")).ToList();
        }

        foreach (var source in sources)
        {
            results.Add(MoveOne(root, source, destFull, newName, policy, dryRun, isBusy));
        }

        return results;
    }

    /// <summary>
    /// Supprime des éléments (ou en donne l'aperçu) : mis à la corbeille du dossier racine, ou définitivement.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="sources">Éléments à supprimer (relatifs).</param>
    /// <param name="mode">"trash" (corbeille) ou "permanent".</param>
    /// <param name="deletedBy">Nom de l'utilisateur, noté dans la corbeille.</param>
    /// <param name="dryRun">Vrai pour un aperçu : rien n'est modifié.</param>
    /// <param name="isBusy">Indique qu'un chemin est en cours d'utilisation par un envoi ou un import.</param>
    /// <param name="nowUtc">Date de la suppression.</param>
    /// <returns>Un résultat par élément.</returns>
    public static List<DeleteResult> Delete(LibraryRoot root, IReadOnlyList<string> sources, string mode, string deletedBy, bool dryRun, Func<string, bool> isBusy, DateTime nowUtc)
    {
        var results = new List<DeleteResult>();
        foreach (var source in sources)
        {
            if (!TryResolve(root, source, out var full, out var error))
            {
                results.Add(new DeleteResult(source, "invalid", error, false, 0, 0, 0, null));
                continue;
            }

            var isDir = Directory.Exists(full);
            if (Same(full, root.Path))
            {
                results.Add(new DeleteResult(source, "invalid", "Impossible de supprimer le dossier racine.", true, 0, 0, 0, null));
                continue;
            }

            if (!isDir && !File.Exists(full))
            {
                results.Add(new DeleteResult(source, "invalid", "Introuvable.", false, 0, 0, 0, null));
                continue;
            }

            if (HasLink(root, full))
            {
                results.Add(new DeleteResult(source, "invalid", "Lien symbolique : refusé par sécurité.", isDir, 0, 0, 0, null));
                continue;
            }

            if (isBusy(full))
            {
                results.Add(new DeleteResult(source, "invalid", "Envoi ou import en cours dans cet élément.", isDir, 0, 0, 0, null));
                continue;
            }

            var stats = Stats(full);
            var (listing, listingTruncated) = Listing(full, 500);
            if (dryRun)
            {
                results.Add(new DeleteResult(source, "ok", null, isDir, stats.Files, stats.Dirs, stats.Bytes, null, listing, listingTruncated));
                continue;
            }

            try
            {
                if (mode == "permanent")
                {
                    if (isDir)
                    {
                        Directory.Delete(full, recursive: true);
                    }
                    else
                    {
                        File.Delete(full);
                    }

                    results.Add(new DeleteResult(source, "deleted", null, isDir, stats.Files, stats.Dirs, stats.Bytes, null, listing, listingTruncated));
                }
                else
                {
                    var id = nowUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
                    var trashRoot = Path.Combine(root.Path, TrashDirName);
                    var itemDir = Path.Combine(trashRoot, id);
                    var data = Path.Combine(itemDir, "data");
                    Directory.CreateDirectory(data);

                    // Jellyfin ignore un dossier qui contient un fichier .ignore : la corbeille n'apparaît jamais dans la bibliothèque.
                    var ignore = Path.Combine(trashRoot, ".ignore");
                    if (!File.Exists(ignore))
                    {
                        File.WriteAllText(ignore, string.Empty);
                    }

                    var target = Path.Combine(data, Path.GetFileName(full));
                    if (isDir)
                    {
                        Directory.Move(full, target);
                    }
                    else
                    {
                        File.Move(full, target);
                    }

                    var meta = new TrashMeta { Id = id, Root = root.Key, OriginalPath = Relative(root, full), Name = Path.GetFileName(full), IsDir = isDir, Files = stats.Files, Bytes = stats.Bytes, DeletedAt = nowUtc, DeletedBy = deletedBy };
                    File.WriteAllText(Path.Combine(itemDir, "meta.json"), JsonSerializer.Serialize(meta));
                    results.Add(new DeleteResult(source, "trashed", null, isDir, stats.Files, stats.Dirs, stats.Bytes, id, listing, listingTruncated));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new DeleteResult(source, "error", ex.Message, isDir, stats.Files, stats.Dirs, stats.Bytes, null, listing, listingTruncated));
            }
        }

        return results;
    }

    /// <summary>
    /// Éléments de la corbeille d'un dossier racine, les plus récents d'abord.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <returns>Éléments.</returns>
    public static List<TrashEntry> ListTrash(LibraryRoot root)
    {
        var list = new List<TrashEntry>();
        var trashRoot = Path.Combine(root.Path, TrashDirName);
        if (!Directory.Exists(trashRoot))
        {
            return list;
        }

        foreach (var dir in Directory.EnumerateDirectories(trashRoot))
        {
            if (ReadMeta(dir) is { } m)
            {
                list.Add(new TrashEntry(m.Id, m.Root, m.OriginalPath, m.IsDir, m.Files, m.Bytes, m.DeletedAt, m.DeletedBy));
            }
        }

        return list.OrderByDescending(e => e.DeletedAt).ToList();
    }

    /// <summary>
    /// Remet des éléments de la corbeille à leur place d'origine (sous un autre nom si la place est prise).
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="ids">Identifiants dans la corbeille.</param>
    /// <returns>Un résultat par élément.</returns>
    public static List<MoveResult> Restore(LibraryRoot root, IReadOnlyList<string> ids)
    {
        var results = new List<MoveResult>();
        foreach (var id in ids)
        {
            if (!TrashId.IsMatch(id))
            {
                results.Add(Invalid(id, "Identifiant invalide."));
                continue;
            }

            var itemDir = Path.Combine(root.Path, TrashDirName, id);
            if (ReadMeta(itemDir) is not { } meta)
            {
                results.Add(Invalid(id, "Introuvable dans la corbeille."));
                continue;
            }

            if (!TryResolve(root, meta.OriginalPath, out var target, out var error))
            {
                results.Add(Invalid(meta.OriginalPath, error));
                continue;
            }

            var data = Path.Combine(itemDir, "data", meta.Name);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var renamed = false;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    target = UniquePath(target, " (restauré)");
                    renamed = true;
                }

                if (meta.IsDir)
                {
                    Directory.Move(data, target);
                }
                else
                {
                    File.Move(data, target);
                }

                Directory.Delete(itemDir, recursive: true);
                results.Add(new MoveResult(meta.OriginalPath, Relative(root, target), renamed ? "renamed" : "moved", renamed ? "La place d'origine était prise : restauré sous un autre nom." : null, meta.Files, meta.Bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new MoveResult(meta.OriginalPath, null, "error", ex.Message, meta.Files, meta.Bytes));
            }
        }

        return results;
    }

    /// <summary>
    /// Supprime définitivement des éléments de la corbeille.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="ids">Identifiants dans la corbeille.</param>
    /// <returns>Éléments purgés, avec leur taille.</returns>
    public static List<TrashEntry> Purge(LibraryRoot root, IReadOnlyList<string> ids)
    {
        var purged = new List<TrashEntry>();
        foreach (var id in ids.Where(i => TrashId.IsMatch(i)))
        {
            var itemDir = Path.Combine(root.Path, TrashDirName, id);
            var meta = ReadMeta(itemDir);
            try
            {
                if (Directory.Exists(itemDir))
                {
                    Directory.Delete(itemDir, recursive: true);
                    if (meta is not null)
                    {
                        purged.Add(new TrashEntry(meta.Id, meta.Root, meta.OriginalPath, meta.IsDir, meta.Files, meta.Bytes, meta.DeletedAt, meta.DeletedBy));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un élément verrouillé sera retenté à la prochaine purge.
            }
        }

        return purged;
    }

    /// <summary>
    /// Purge de la corbeille : supprime les éléments plus vieux que <paramref name="days"/> jours.
    /// </summary>
    /// <param name="roots">Dossiers racine.</param>
    /// <param name="days">Durée de conservation (0 : rien n'est purgé).</param>
    /// <param name="nowUtc">Date courante.</param>
    /// <returns>Éléments purgés.</returns>
    public static List<TrashEntry> PurgeOlderThan(IEnumerable<LibraryRoot> roots, int days, DateTime nowUtc)
    {
        var purged = new List<TrashEntry>();
        if (days <= 0)
        {
            return purged;
        }

        foreach (var root in roots)
        {
            var old = ListTrash(root).Where(e => nowUtc - e.DeletedAt > TimeSpan.FromDays(days)).Select(e => e.Id).ToList();
            purged.AddRange(Purge(root, old));
        }

        return purged;
    }

    /// <summary>
    /// Repère les anomalies d'un dossier racine : dossiers vides, fichiers partiels abandonnés, fichiers inconnus, et pour la musique
    /// morceaux sans pochette (ni intégrée au fichier, ni image dans le dossier) et dossiers d'album au nom quasi identique.
    /// </summary>
    /// <param name="root">Dossier racine.</param>
    /// <param name="kindOf">Type d'un fichier d'après son extension.</param>
    /// <param name="isBusy">Indique qu'un chemin est en cours d'utilisation.</param>
    /// <param name="nowUtc">Date courante.</param>
    /// <param name="musicChecks">Vrai pour une bibliothèque musique (contrôles de pochette et d'albums en double).</param>
    /// <param name="hasEmbeddedCover">Indique si un fichier audio contient une pochette intégrée (null : seule une image dans le dossier compte).</param>
    /// <param name="cap">Nombre maximal d'éléments parcourus.</param>
    /// <returns>Rapport.</returns>
    public static HealthReport Health(LibraryRoot root, Func<string, string> kindOf, Func<string, bool> isBusy, DateTime nowUtc, bool musicChecks, Func<string, bool>? hasEmbeddedCover, int cap = 100_000)
    {
        var empty = new List<string>();
        var parts = new List<string>();
        var noCover = new List<string>();
        var unknown = new List<string>();
        var duplicates = new List<DuplicateAlbumGroup>();
        var seen = 0;
        var coverReads = 0;
        var truncated = false;

        // Renvoie le nombre de fichiers contenus (récursivement), pour repérer les dossiers vides.
        int Walk(string dir)
        {
            var total = 0;
            var audioFiles = new List<string>();
            var hasCover = false;
            var subdirs = new List<string>();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 1;
            }

            foreach (var child in children)
            {
                if (++seen > cap)
                {
                    truncated = true;
                    return Math.Max(total, 1);
                }

                if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    total++;
                    continue;
                }

                if (child is DirectoryInfo sub)
                {
                    if (string.Equals(sub.Name, TrashDirName, StringComparison.Ordinal))
                    {
                        total++;
                        continue;
                    }

                    subdirs.Add(sub.Name);
                    var inside = Walk(sub.FullName);
                    if (inside == 0 && empty.Count < 200)
                    {
                        empty.Add(Relative(root, sub.FullName));
                    }

                    total += inside;
                    continue;
                }

                total++;
                var name = child.Name;
                if (name.StartsWith(".mu-", StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal))
                {
                    if (nowUtc - child.LastWriteTimeUtc > TimeSpan.FromHours(1) && !isBusy(child.FullName) && parts.Count < 200)
                    {
                        parts.Add(Relative(root, child.FullName));
                    }

                    continue;
                }

                var kind = kindOf(child.Extension.ToLowerInvariant());
                if (kind == "audio")
                {
                    audioFiles.Add(child.FullName);
                }

                if (ImageExtensions.Contains(child.Extension.ToLowerInvariant()) && CoverNames.Contains(Path.GetFileNameWithoutExtension(name).ToLowerInvariant()))
                {
                    hasCover = true;
                }

                if (kind == "other" && !name.StartsWith('.') && unknown.Count < 200)
                {
                    unknown.Add(Relative(root, child.FullName));
                }
            }

            if (musicChecks && audioFiles.Count > 0 && !hasCover && noCover.Count < 200)
            {
                // Une pochette intégrée à chaque fichier (morceau seul, miniature d'une vidéo YouTube…) suffit : seul un fichier sans aucune image est signalé.
                var missing = hasEmbeddedCover is null;
                if (hasEmbeddedCover is not null)
                {
                    foreach (var file in audioFiles.Take(40))
                    {
                        if (coverReads >= 5000)
                        {
                            truncated = true;
                            break;
                        }

                        coverReads++;
                        if (!hasEmbeddedCover(file))
                        {
                            missing = true;
                            break;
                        }
                    }
                }

                if (missing)
                {
                    noCover.Add(Relative(root, dir));
                }
            }

            if (musicChecks && subdirs.Count > 1 && duplicates.Count < 100)
            {
                foreach (var group in subdirs.GroupBy(AlbumKey).Where(g => g.Key.Length > 0 && g.Count() > 1))
                {
                    duplicates.Add(new DuplicateAlbumGroup(Relative(root, dir), group.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()));
                }
            }

            return total;
        }

        Walk(root.Path);
        return new HealthReport(empty, parts, noCover, unknown, truncated, duplicates);
    }

    private static MoveResult Invalid(string source, string? message) => new(source, null, "invalid", message, 0, 0);

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), PathComparison);

    private static TrashMeta? ReadMeta(string itemDir)
    {
        try
        {
            var file = Path.Combine(itemDir, "meta.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string UniquePath(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var first = Path.Combine(dir, name + suffix + ext);
        if (!File.Exists(first) && !Directory.Exists(first))
        {
            return first;
        }

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}{suffix} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}{suffix} {Guid.NewGuid():N}{ext}");
    }

    private static string UniqueCopyName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static MoveResult MoveOne(LibraryRoot root, string sourceRel, string destFull, string? newName, string policy, bool dryRun, Func<string, bool> isBusy)
    {
        if (!TryResolve(root, sourceRel, out var src, out var error))
        {
            return Invalid(sourceRel, error);
        }

        if (Same(src, root.Path))
        {
            return Invalid(sourceRel, "Impossible de déplacer le dossier racine.");
        }

        var isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src))
        {
            return Invalid(sourceRel, "Introuvable.");
        }

        if (HasLink(root, src))
        {
            return Invalid(sourceRel, "Lien symbolique : refusé par sécurité.");
        }

        if (isBusy(src))
        {
            return Invalid(sourceRel, "Envoi ou import en cours dans cet élément.");
        }

        var name = newName is null ? Path.GetFileName(src) : isDir ? PathBuilder.SanitizeSegment(newName, "dossier") : PathBuilder.SanitizeFileName(newName, "fichier");
        if (name.StartsWith(".mu-", StringComparison.Ordinal))
        {
            return Invalid(sourceRel, "Nom réservé au plugin.");
        }

        var target = Path.Combine(destFull, name);
        if (Same(src, target))
        {
            return Invalid(sourceRel, "Déjà à cet endroit sous ce nom.");
        }

        if (isDir && PathBuilder.IsInside(src, target))
        {
            return Invalid(sourceRel, "Un dossier ne peut pas être déplacé dans lui-même.");
        }

        var stats = Stats(src);
        var listing = isDir && !dryRun ? Listing(src, 200).Files : null;
        var exists = File.Exists(target) || Directory.Exists(target);
        try
        {
            if (!exists)
            {
                if (dryRun)
                {
                    return new MoveResult(sourceRel, Relative(root, target), "ok", null, stats.Files, stats.Bytes);
                }

                Directory.CreateDirectory(destFull);
                MovePath(src, target, isDir);
                return new MoveResult(sourceRel, Relative(root, target), "moved", null, stats.Files, stats.Bytes, listing);
            }

            if (isDir && Directory.Exists(target))
            {
                if (dryRun)
                {
                    return new MoveResult(sourceRel, Relative(root, target), "merge", "Un dossier du même nom existe : les contenus seront fusionnés.", stats.Files, stats.Bytes);
                }

                var (moved, skipped) = Merge(src, target, policy);
                var message = $"{moved} fichier(s) déplacé(s)" + (skipped > 0 ? $", {skipped} ignoré(s) (déjà présents)" : string.Empty);
                return new MoveResult(sourceRel, Relative(root, target), "merged", message, moved, stats.Bytes, listing);
            }

            if (isDir != Directory.Exists(target))
            {
                return Invalid(sourceRel, "Un élément de type différent porte déjà ce nom.");
            }

            // Fichier contre fichier.
            if (dryRun)
            {
                var what = policy switch { "overwrite" => "sera écrasé", "rename" => "sera renommé avec un suffixe", _ => "sera ignoré" };
                return new MoveResult(sourceRel, Relative(root, target), "conflict", "Un fichier du même nom existe : celui-ci " + what + ".", stats.Files, stats.Bytes);
            }

            Directory.CreateDirectory(destFull);
            switch (policy)
            {
                case "overwrite":
                    File.Move(src, target, overwrite: true);
                    return new MoveResult(sourceRel, Relative(root, target), "moved", "Fichier existant écrasé.", 1, stats.Bytes);
                case "rename":
                    var renamed = UniqueCopyName(target);
                    File.Move(src, renamed);
                    return new MoveResult(sourceRel, Relative(root, renamed), "renamed", "Un fichier du même nom existait : renommé.", 1, stats.Bytes);
                default:
                    return new MoveResult(sourceRel, Relative(root, target), "skipped", "Un fichier du même nom existe déjà : ignoré.", 0, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveResult(sourceRel, Relative(root, target), "error", ex.Message, 0, 0);
        }
    }

    private static void MovePath(string src, string target, bool isDir)
    {
        if (isDir)
        {
            Directory.Move(src, target);
        }
        else
        {
            File.Move(src, target);
        }
    }

    // Fusionne le contenu de src dans dst (dossier existant). Renvoie le nombre de fichiers déplacés et ignorés.
    private static (int Moved, int Skipped) Merge(string src, string dst, string policy)
    {
        int moved = 0, skipped = 0;
        foreach (var child in new DirectoryInfo(src).EnumerateFileSystemInfos().ToList())
        {
            var target = Path.Combine(dst, child.Name);
            if (child is DirectoryInfo dir)
            {
                if (Directory.Exists(target))
                {
                    var (m, s) = Merge(dir.FullName, target, policy);
                    moved += m;
                    skipped += s;
                }
                else if (File.Exists(target))
                {
                    skipped += Stats(dir.FullName).Files;
                }
                else
                {
                    var files = Stats(dir.FullName).Files;
                    Directory.Move(dir.FullName, target);
                    moved += files;
                }

                continue;
            }

            if (!File.Exists(target) && !Directory.Exists(target))
            {
                File.Move(child.FullName, target);
                moved++;
                continue;
            }

            switch (policy)
            {
                case "overwrite" when File.Exists(target):
                    File.Move(child.FullName, target, overwrite: true);
                    moved++;
                    break;
                case "rename":
                    File.Move(child.FullName, UniqueCopyName(target));
                    moved++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        // Le dossier d'origine disparaît s'il est vide après la fusion.
        if (!new DirectoryInfo(src).EnumerateFileSystemInfos().Any())
        {
            Directory.Delete(src);
        }

        return (moved, skipped);
    }
}
