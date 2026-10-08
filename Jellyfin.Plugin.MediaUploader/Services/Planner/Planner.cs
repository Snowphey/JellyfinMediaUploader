namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Calcule, pour un lot de fichiers, ce qui sera fait : type, métadonnées, destination, association des sous-titres,
/// regroupement (album, film, série) et niveau de confiance. Ne touche pas au disque (il le consulte seulement) :
/// le même calcul sert à l'aperçu et à l'envoi réel.
/// </summary>
public static class Planner
{
    private sealed class Work
    {
        public required PlanFile File { get; init; }

        public required PlanItem Item { get; init; }

        public required string Ext { get; init; }

        public required string ClientDir { get; init; }

        public ItemOverride? Ov { get; set; }

        public bool IsAudio { get; set; }

        public bool IsVideo { get; set; }

        public Detection? Det { get; set; }

        public string? Dir { get; set; }

        public string? FinalName { get; set; }

        public bool Placed { get; set; }

        public bool Rejected => Item.Status is "skipped" or "error";
    }

    /// <summary>
    /// Construit le plan d'un lot.
    /// </summary>
    /// <param name="files">Fichiers du lot.</param>
    /// <param name="overrides">Corrections de l'utilisateur, par identifiant de fichier.</param>
    /// <param name="mode">Type choisi par l'utilisateur : "auto", "music", "movie" ou "series".</param>
    /// <param name="o">Réglages.</param>
    /// <param name="engine">Moteur de détection.</param>
    /// <param name="fs">Accès au disque.</param>
    /// <returns>Plan.</returns>
    public static Plan Build(
        IReadOnlyList<PlanFile> files,
        IReadOnlyDictionary<string, ItemOverride> overrides,
        string? mode,
        PlannerOptions o,
        DetectionEngine engine,
        IFileProbe fs)
    {
        var forcedMode = (mode ?? "auto").Trim().ToLowerInvariant();
        if (forcedMode is not ("music" or "movie" or "series"))
        {
            forcedMode = "auto";
        }

        var works = new List<Work>();
        foreach (var f in files)
        {
            overrides.TryGetValue(f.Id, out var ov);
            works.Add(Classify(f, ov, forcedMode, o, engine));
        }

        // 0. Un album = un seul dossier d'artiste, même si les tags des pistes citent des artistes différents.
        UnifyAlbumArtists(works);

        // 1. Fichiers principaux (audio, vidéo) : destination et nom.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in works.Where(w => w.Item.Role == "main" && !w.Rejected))
        {
            PlaceOwn(w, o, fs, taken);
        }

        // 1 bis. Doublons de musique (même artiste et même titre dans les tags), dans le lot et dans la bibliothèque.
        DetectDuplicates(works, o, fs, taken);

        // 2. Sous-titres : rattachés à leur vidéo (même dossier, nom de la vidéo), sinon rangés seuls.
        var mains = works.Where(w => w.Item.Role == "main" && w.IsVideo && !w.Rejected && w.Placed).ToList();
        foreach (var w in works.Where(w => w.Item.Role == "subtitle" && !w.Rejected))
        {
            PlaceSubtitle(w, mains, o, engine, fs, taken);
        }

        // 3. Annexes (pochettes, .nfo, .lrc) : elles suivent les fichiers de leur dossier d'origine.
        var placedMains = works.Where(w => w.Item.Role == "main" && !w.Rejected && w.Placed).ToList();
        foreach (var w in works.Where(w => w.Item.Role == "extra" && !w.Rejected))
        {
            PlaceExtra(w, placedMains, forcedMode, o, fs, taken);
        }

        return Group(works, o);
    }

    /// <summary>
    /// Nom d'un sous-titre rattaché à une vidéo : nom de la vidéo + suffixe de langue + extension du sous-titre.
    /// </summary>
    /// <param name="videoFileName">Nom final de la vidéo.</param>
    /// <param name="langSuffix">Suffixe de langue (« .fr »).</param>
    /// <param name="subtitleExt">Extension du sous-titre.</param>
    /// <returns>Nom du sous-titre.</returns>
    public static string SubtitleName(string videoFileName, string langSuffix, string subtitleExt)
    {
        return Path.GetFileNameWithoutExtension(videoFileName) + langSuffix + subtitleExt;
    }

    private static Work Classify(PlanFile f, ItemOverride? ov, string mode, PlannerOptions o, DetectionEngine engine)
    {
        var clientPath = NameTools.NormalizePath(f.ClientPath);
        var name = NameTools.FileNameOf(clientPath);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var dirIdx = clientPath.LastIndexOf('/');
        var item = new PlanItem { Id = f.Id, Name = name, ClientPath = clientPath, Size = f.Size };
        var w = new Work { File = f, Item = item, Ext = ext, ClientDir = dirIdx < 0 ? string.Empty : clientPath[..dirIdx], Ov = ov };

        void Reject(string status, string message)
        {
            item.Status = status;
            item.Notes.Add(message);
        }

        if (name.Length == 0)
        {
            Reject("skipped", "Nom de fichier vide");
            return w;
        }

        if (o.MaxFileBytes > 0 && f.Size > o.MaxFileBytes)
        {
            Reject("skipped", $"Fichier trop gros (max {o.MaxFileBytes / (1024 * 1024)} Mo)");
            return w;
        }

        if (o.Audio.Contains(ext))
        {
            w.IsAudio = true;
            item.Role = "main";
        }
        else if (o.Video.Contains(ext))
        {
            w.IsVideo = true;
            item.Role = "main";
        }
        else if (o.Extra.Contains(ext))
        {
            item.Role = NameTools.IsSubtitle(ext) ? "subtitle" : "extra";
        }
        else
        {
            Reject("skipped", $"Extension non autorisée : '{ext}'");
            return w;
        }

        if (w.IsAudio && mode is "movie" or "series")
        {
            Reject("skipped", "Fichier audio : incompatible avec le type Film / Série choisi");
            return w;
        }

        if ((w.IsVideo || item.Role == "subtitle") && mode == "music")
        {
            Reject("skipped", "Fichier vidéo ou sous-titre : incompatible avec le type Musique choisi");
            return w;
        }

        if (item.Role == "extra")
        {
            // Rattachée plus tard aux fichiers de son dossier.
            item.Kind = ext == ".lrc" ? "music" : null;
            return w;
        }

        if (w.IsAudio)
        {
            var det = engine.DetectMusic(clientPath, f.Tags);
            item.TagsPending = !f.Analyzed;
            if (item.TagsPending)
            {
                det.Notes.Add("Tags lus après l'arrivée du fichier");
            }

            item.Manual = ApplyOverride(det, ov);
            if (f.Analyzed)
            {
                if (det.Artist is null)
                {
                    item.Missing.Add("artist");
                }

                if (det.Album is null)
                {
                    item.Missing.Add("album");
                }
            }

            w.Det = det;
            Fill(item, det);
            return w;
        }

        var forced = ov?.Kind is "movie" or "series" ? ov.Kind : (mode is "movie" or "series" ? mode : null);
        var vdet = engine.DetectVideo(clientPath, forced);
        item.Manual = ApplyOverride(vdet, ov);
        w.Det = vdet;
        Fill(item, vdet);
        item.LangSuffix = NameTools.IsSubtitle(ext) ? NameTools.LangSuffixOf(name) : string.Empty;
        return w;
    }

    private static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);

    // Applique les corrections de l'utilisateur (elles remplacent ce qui a été déduit). Retourne vrai si quelque chose a été corrigé.
    private static bool ApplyOverride(Detection det, ItemOverride? ov)
    {
        if (ov is null)
        {
            return false;
        }

        var music = det.Kind == "music";
        var applied = false;
        if (!Blank(ov.Title) && !music)
        {
            det.Title = ov.Title!.Trim();
            applied = true;
        }

        if (ov.Year is not null)
        {
            det.Year = ov.Year == 0 ? null : ov.Year;
            applied = true;
        }

        if (ov.Season is not null)
        {
            det.Season = ov.Season;
            applied = true;
        }

        if (ov.Episode is not null)
        {
            det.Episode = ov.Episode;
            applied = true;
        }

        if (!Blank(ov.Artist))
        {
            det.Artist = ov.Artist!.Trim();
            applied = true;
        }

        if (!Blank(ov.Album))
        {
            det.Album = ov.Album!.Trim();
            applied = true;
        }

        if (!applied)
        {
            return false;
        }

        // Une série corrigée à la main sans saison : on suppose la saison 1 plutôt que de ranger à plat.
        if (det.Kind == "series" && det.Season is null && !string.IsNullOrWhiteSpace(det.Title))
        {
            det.Season = 1;
        }

        det.Confidence = "sure";
        det.RuleName = "Corrigé à la main";
        det.Notes.RemoveAll(n => n.StartsWith("Aucune règle", StringComparison.Ordinal) || n.Contains("supposée", StringComparison.Ordinal)
            || n.StartsWith("Pas d'artiste", StringComparison.Ordinal) || n.StartsWith("Tags illisibles", StringComparison.Ordinal));
        return true;
    }

    private static void Fill(PlanItem item, Detection det)
    {
        item.Kind = det.Kind;
        item.Title = det.Title;
        item.Year = det.Year;
        item.Season = det.Season;
        item.Episode = det.Episode;
        item.Artist = det.Artist;
        item.Album = det.Album;
        item.Confidence = det.Confidence;
        item.RuleName = det.RuleName;
        item.Notes.AddRange(det.Notes);
    }

    private static string? RootOf(string? kind, PlannerOptions o) => kind switch
    {
        "music" => o.MusicRoot,
        "movie" => o.MoviesRoot,
        "series" => o.ShowsRoot,
        _ => null
    };

    private static string KindLabel(string? kind) => kind switch
    {
        "music" => "musique",
        "movie" => "films",
        "series" => "séries et animés",
        _ => "?"
    };

    private static bool Structured(string? kind, PlannerOptions o) => kind switch
    {
        "music" => o.MusicStructured,
        "movie" => o.MoviesStructured,
        "series" => o.ShowsStructured,
        _ => false
    };

    // Dossier de destination d'après ce qui a été reconnu (la racine si on ne sait pas).
    private static string DirFor(string kind, string root, Detection det, bool structured)
    {
        if (!structured)
        {
            return root;
        }

        switch (kind)
        {
            case "music":
                return PathBuilder.MusicDir(root, det.Artist, det.Album);
            case "movie":
                return string.IsNullOrWhiteSpace(det.Title) ? root : PathBuilder.MovieDir(root, det.Title, det.Year);
            default:
                return string.IsNullOrWhiteSpace(det.Title) ? root : PathBuilder.SeriesDir(root, det.Title, det.Year, det.Season);
        }
    }

    private static void PlaceOwn(Work w, PlannerOptions o, IFileProbe fs, HashSet<string> taken)
    {
        var item = w.Item;
        var det = w.Det!;
        var kind = det.Kind ?? "movie";
        var root = RootOf(kind, o);
        if (string.IsNullOrWhiteSpace(root))
        {
            item.Status = "error";
            item.Notes.Add($"Le dossier « {KindLabel(kind)} » n'est pas configuré dans les paramètres du plugin.");
            return;
        }

        var structured = Structured(kind, o);
        w.Dir = DirFor(kind, root, det, structured);
        if (structured && !det.HasIdentity)
        {
            item.Notes.Add(kind == "music" ? "Pas d'artiste ni d'album : placé à la racine" : "Titre non reconnu : placé à la racine");
        }

        Finish(w, root, PathBuilder.SanitizeSegment(item.Name, "file"), o, fs, taken);
    }

    // Applique le nom final, résout les collisions et remplit la destination.
    private static void Finish(Work w, string root, string fileName, PlannerOptions o, IFileProbe fs, HashSet<string> taken)
    {
        var item = w.Item;
        var target = Path.Combine(w.Dir!, fileName);

        if (!PathBuilder.IsInside(root, target))
        {
            item.Status = "error";
            item.Notes.Add("Chemin de destination invalide");
            return;
        }

        var exists = fs.Exists(target);
        if (taken.Contains(target) || (exists && !o.Overwrite))
        {
            var unique = Unique(target, p => taken.Contains(p) || fs.Exists(p));
            item.Notes.Add(exists
                ? $"Un fichier du même nom existe déjà : sera enregistré sous « {Path.GetFileName(unique)} »"
                : $"Même nom qu'un autre fichier du lot : sera enregistré sous « {Path.GetFileName(unique)} »");
            Raise(item, "guess");
            target = unique;
        }
        else if (exists)
        {
            item.Notes.Add("Écrasera le fichier existant");
            Raise(item, "guess");
        }

        taken.Add(target);
        w.Placed = true;
        w.FinalName = Path.GetFileName(target);
        w.Dir = Path.GetDirectoryName(target)!;
        item.Root = root;
        item.DestFull = target;
        item.NewName = w.FinalName;
        item.Dest = Path.GetRelativePath(root, target).Replace('\\', '/');
    }

    // Monte le niveau de doute d'un élément (sure < guess < unknown), sans jamais le baisser.
    private static void Raise(PlanItem item, string confidence)
    {
        var rank = new Dictionary<string, int> { ["sure"] = 0, ["guess"] = 1, ["unknown"] = 2 };
        if (rank[confidence] > rank[item.Confidence])
        {
            item.Confidence = confidence;
        }
    }

    private static string Unique(string path, Func<string, bool> isTaken)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static bool SameVideo(Detection sub, Detection video, bool sameClientDir)
    {
        if (sub.Kind != video.Kind)
        {
            return false;
        }

        var ts = NameTools.TitleKey(sub.Title);
        var tv = NameTools.TitleKey(video.Title);
        if (sub.Kind == "movie")
        {
            return ts.Length > 0 && ts == tv && (sub.Year is null || video.Year is null || sub.Year == video.Year);
        }

        if (sub.Season is null || sub.Episode is null || sub.Season != video.Season || sub.Episode != video.Episode)
        {
            return false;
        }

        return (ts.Length > 0 && ts == tv) || ((ts.Length == 0 || tv.Length == 0) && sameClientDir);
    }

    private static void PlaceSubtitle(Work w, List<Work> videos, PlannerOptions o, DetectionEngine engine, IFileProbe fs, HashSet<string> taken)
    {
        var item = w.Item;
        var det = w.Det!;
        var kind = det.Kind ?? "movie";
        var root = RootOf(kind, o);
        if (string.IsNullOrWhiteSpace(root))
        {
            item.Status = "error";
            item.Notes.Add($"Le dossier « {KindLabel(kind)} » n'est pas configuré dans les paramètres du plugin.");
            return;
        }

        var ownDir = DirFor(kind, root, det, Structured(kind, o));
        string? pairName = null;
        string? pairDir = null;
        var pair = new PlanPair();

        // a) Choix explicite de l'utilisateur.
        if (!string.IsNullOrWhiteSpace(w.Ov?.PairWith))
        {
            var chosen = videos.FirstOrDefault(v => v.Item.Id == w.Ov!.PairWith);
            if (chosen is not null)
            {
                pair.WithId = chosen.Item.Id;
                pair.Source = "manual";
                pairName = chosen.FinalName;
                pairDir = chosen.Dir;
            }
        }

        // b) Vidéo du même lot.
        if (pairName is null)
        {
            var candidates = videos
                .Where(v => v.Det is not null && v.Det.Kind == det.Kind
                    && SameVideo(det, v.Det, string.Equals(v.ClientDir, w.ClientDir, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(v => v.Item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count > 0)
            {
                var chosen = candidates[0];
                pair.WithId = chosen.Item.Id;
                pair.Source = "batch";
                pair.Ambiguous = candidates.Count > 1;
                pairName = chosen.FinalName;
                pairDir = chosen.Dir;
            }
        }

        // c) Vidéo déjà présente dans le dossier de destination.
        if (pairName is null && det.HasIdentity)
        {
            foreach (var path in fs.ListFiles(ownDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!o.Video.Contains(ext))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                var vdet = engine.DetectVideo(rel, det.Kind);
                if (vdet.HasIdentity && SameVideo(det, vdet, true))
                {
                    pair.Source = "library";
                    pairName = Path.GetFileName(path);
                    pairDir = ownDir;
                    break;
                }
            }
        }

        w.Dir = pairDir ?? ownDir;
        var fileName = PathBuilder.SanitizeSegment(item.Name, "file");

        if (pairName is not null)
        {
            pair.WithName = pairName;
            item.Pair = pair;
            var videoStem = Path.GetFileNameWithoutExtension(pairName);
            var alreadyOk = string.Equals(NameTools.StemOf(item.Name), videoStem, StringComparison.OrdinalIgnoreCase);
            if (!alreadyOk && o.RenameSubtitles && w.Ov?.KeepName != true)
            {
                fileName = PathBuilder.SanitizeSegment(SubtitleName(pairName, item.LangSuffix, w.Ext), "file");
                pair.Renamed = true;
            }
            else if (!alreadyOk)
            {
                item.Notes.Add("Nom d'origine conservé : Jellyfin n'associera pas ce sous-titre à la vidéo tant que les noms diffèrent");
            }

            if (pair.Source == "library")
            {
                item.Notes.Add("Associé à une vidéo déjà présente dans la bibliothèque");
                Raise(item, "guess");
            }
            else if (pair.Source == "manual")
            {
                item.Confidence = "sure";
            }

            if (pair.Ambiguous)
            {
                item.Notes.Add("Plusieurs vidéos du lot conviennent : à confirmer");
                Raise(item, "guess");
            }
        }
        else
        {
            item.Notes.Add("Aucune vidéo associée trouvée : Jellyfin ne l'affichera que si une vidéo du même nom est dans le dossier");
            Raise(item, "guess");
            if (Structured(kind, o) && !det.HasIdentity)
            {
                item.Notes.Add("Titre non reconnu : placé à la racine");
            }
        }

        Finish(w, root, fileName, o, fs, taken);
    }

    private static void PlaceExtra(Work w, List<Work> mains, string mode, PlannerOptions o, IFileProbe fs, HashSet<string> taken)
    {
        var item = w.Item;
        string? kind = null;
        string? root = null;
        var siblings = mains.Where(m => string.Equals(m.ClientDir, w.ClientDir, StringComparison.OrdinalIgnoreCase)).ToList();

        // Fichier .lrc : il suit la piste qui porte le même nom.
        var stem = Path.GetFileNameWithoutExtension(item.Name);
        var sameStem = siblings.Where(m => string.Equals(Path.GetFileNameWithoutExtension(m.Item.Name), stem, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = sameStem.Count > 0 ? sameStem : siblings;
        var dirs = pool.Select(m => m.Dir!).Distinct(StringComparer.Ordinal).ToList();

        // Affiche posée dans le dossier d'une série dont les épisodes sont dans des sous-dossiers : elle va à la racine de la série.
        var viaChildren = false;
        if (dirs.Count == 0 && w.ClientDir.Length > 0)
        {
            var below = mains.Where(m => m.ClientDir.StartsWith(w.ClientDir + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            var tops = below.Select(GroupRootDir).Distinct(StringComparer.Ordinal).ToList();
            if (tops.Count == 1)
            {
                pool = below;
                dirs = tops;
                viaChildren = true;
            }
        }

        if (dirs.Count == 1)
        {
            w.Dir = dirs[0];
            kind = pool[0].Item.Kind;
            root = pool[0].Item.Root;
            item.Kind = kind;

            // Des fichiers déposés « en vrac » n'ont pas de dossier commun qui prouve le lien : on le signale.
            var loose = w.ClientDir.Length == 0;
            item.Confidence = loose ? "guess" : "sure";
            item.Notes.Add(loose
                ? "Rattaché au seul média déposé avec lui (à vérifier)"
                : viaChildren ? "Placé à la racine du dossier des fichiers qu'il contient" : "Suit les fichiers de son dossier d'origine");
        }
        else
        {
            // Pas de fichier lié : racine du type choisi, sinon du type majoritaire du lot.
            kind = mode != "auto" ? mode : null;
            if (kind is null)
            {
                var top = mains.GroupBy(m => m.Item.Kind).OrderByDescending(g => g.Count()).ToList();
                if (top.Count > 0 && (top.Count == 1 || top[0].Count() > top[1].Count()))
                {
                    kind = top[0].Key;
                }
            }

            if (w.Ov?.Kind is "music" or "movie" or "series")
            {
                kind = w.Ov.Kind;
            }

            root = RootOf(kind, o);
            if (kind is null)
            {
                item.Status = "skipped";
                item.Notes.Add("Fichier annexe sans média associé : choisissez le type");
                return;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                item.Status = "error";
                item.Notes.Add($"Le dossier « {KindLabel(kind)} » n'est pas configuré dans les paramètres du plugin.");
                return;
            }

            w.Dir = root;
            item.Kind = kind;
            item.Confidence = "unknown";
            item.Notes.Add(dirs.Count > 1
                ? "Plusieurs destinations possibles dans ce dossier : placé à la racine"
                : "Aucun média associé : placé à la racine");
        }

        Finish(w, root!, PathBuilder.SanitizeSegment(item.Name, "file"), o, fs, taken);
    }

    // Dossier « racine » d'un média : pour une série, le dossier de la série (au-dessus de Season NN).
    private static string GroupRootDir(Work m)
    {
        var dir = m.Dir!;
        if (m.Item.Kind == "series" && m.Item.Root is { } root && m.Item.Dest is { } dest && dest.Count(c => c == '/') >= 2)
        {
            return Path.Combine(root, dest[..dest.IndexOf('/')]);
        }

        return dir;
    }

    private static readonly string[] ArtistSeparators = { ", ", "; ", " & ", " feat. ", " feat ", " ft. ", " ft ", " featuring ", " / " };

    // Artiste commun à plusieurs artistes d'un même album (featurings, invités). Retourne l'artiste et s'il faut le faire vérifier.
    // 1. le plus court s'il ouvre tous les autres (« Kendrick Lamar » / « Kendrick Lamar, SZA ») ;
    // 2. le premier nom, s'il est le même partout (« Duo, X » / « Duo, Y ») ;
    // 3. les noms présents sur toutes les pistes, quel que soit leur ordre (« A, B » / « B, A » / « A ») : à vérifier ;
    // 4. sinon « Various Artists » : à vérifier.
    private static (string Artist, bool Review) CommonArtist(List<string> artists)
    {
        var shortest = artists.OrderBy(a => a.Length).First();
        if (artists.All(a => a.Equals(shortest, StringComparison.OrdinalIgnoreCase)
            || ArtistSeparators.Any(sep => a.StartsWith(shortest + sep, StringComparison.OrdinalIgnoreCase))))
        {
            return (shortest, false);
        }

        List<string> Tokens(string a) => a.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var leads = artists.Select(a => Tokens(a).FirstOrDefault() ?? a).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (leads.Count == 1)
        {
            return (leads[0], false);
        }

        var shared = Tokens(shortest);
        foreach (var a in artists)
        {
            var t = Tokens(a);
            shared = shared.Where(x => t.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return shared.Count > 0 ? (string.Join(", ", shared), true) : ("Various Artists", true);
    }

    private static void UnifyAlbumArtists(List<Work> works)
    {
        var byAlbum = works
            .Where(w => w.IsAudio && !w.Rejected && w.File.Analyzed && w.Det is { Artist: not null, Album: not null, RuleId: null } && Blank(w.Ov?.Artist))
            .GroupBy(w => NameTools.TitleKey(w.Det!.Album))
            .Where(g => g.Key.Length > 0);

        foreach (var g in byAlbum)
        {
            var artists = g.Select(w => w.Det!.Artist!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (artists.Count < 2)
            {
                continue;
            }

            var (artist, review) = CommonArtist(artists);
            foreach (var w in g.Where(w => !string.Equals(w.Det!.Artist, artist, StringComparison.OrdinalIgnoreCase)))
            {
                w.Item.Notes.Add(review
                    ? $"Artistes différents sur cet album ({artists.Count}) : regroupés sous « {artist} » (à vérifier)"
                    : $"Artiste unifié avec les autres morceaux de l'album : « {artist} » (tags : « {w.Det!.Artist} »)");
                w.Det!.Artist = artist;
                w.Item.Artist = artist;
                if (review)
                {
                    Raise(w.Item, "guess");
                }
            }

            if (review)
            {
                // Le choix est incertain : on propose les artistes possibles à l'utilisateur.
                var choices = new[] { artist }.Concat(artists).Append("Various Artists").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var w in g)
                {
                    w.Item.ArtistChoices = choices;
                }

                foreach (var w in g.Where(w => w.Item.Artist == artist && !w.Item.Notes.Any(n => n.StartsWith("Artistes différents", StringComparison.Ordinal))))
                {
                    w.Item.Notes.Add($"Artistes différents sur cet album : regroupés sous « {artist} » (à vérifier)");
                    Raise(w.Item, "guess");
                }
            }
        }
    }

    private static string? MusicKey(string? artist, string? title)
    {
        var a = NameTools.TitleKey(artist);
        var t = NameTools.TitleKey(title);
        return a.Length > 0 && t.Length > 0 ? a + "|" + t : null;
    }

    private static void DetectDuplicates(List<Work> works, PlannerOptions o, IFileProbe fs, HashSet<string> taken)
    {
        var seen = new Dictionary<string, Work>(StringComparer.Ordinal);
        var library = new Dictionary<string, List<(string Rel, string Key)>>(StringComparer.Ordinal);

        foreach (var w in works.Where(w => w.Item.Role == "main" && w.IsAudio && !w.Rejected && w.Placed && w.File.Analyzed))
        {
            var title = !Blank(w.Ov?.Title) ? w.Ov!.Title : w.File.Tags?.Title;
            var key = MusicKey(NameTools.DedupeArtists(w.File.Tags?.Artist) ?? w.Item.Artist, title);
            if (key is null)
            {
                continue;
            }

            PlanDuplicate? dup = null;
            if (seen.TryGetValue(key, out var first))
            {
                dup = new PlanDuplicate { With = first.Item.Name, Source = "batch" };
            }
            else
            {
                seen[key] = w;
                var root = w.Item.Root!;
                var dir = o.MusicStructured && !string.IsNullOrWhiteSpace(w.Item.Artist)
                    ? Path.Combine(root, PathBuilder.SanitizeSegment(w.Item.Artist, "Unknown Artist"))
                    : root;
                if (!library.TryGetValue(dir, out var known))
                {
                    var files = o.MusicStructured && dir != root ? fs.ListFilesDeep(dir) : fs.ListFiles(dir);
                    known = new List<(string, string)>();
                    foreach (var path in files.Where(p => o.Audio.Contains(Path.GetExtension(p).ToLowerInvariant())))
                    {
                        var tags = fs.ReadTags(path);
                        var k = MusicKey(NameTools.DedupeArtists(tags?.Artist), tags?.Title);
                        if (k is not null)
                        {
                            known.Add((Path.GetRelativePath(root, path).Replace('\\', '/'), k));
                        }
                    }

                    library[dir] = known;
                }

                var hit = known.FirstOrDefault(e => e.Key == key);
                if (hit.Rel is not null)
                {
                    dup = new PlanDuplicate { With = hit.Rel, Source = "library" };
                }
            }

            if (dup is null)
            {
                continue;
            }

            w.Item.Duplicate = dup;
            var where = dup.Source == "batch" ? "dans ce lot" : "dans la bibliothèque";
            if (w.Ov?.Force == true)
            {
                dup.Forced = true;
                w.Item.Notes.Add($"Doublon forcé : même artiste et même titre que « {dup.With} » ({where})");
                Raise(w.Item, "guess");
            }
            else
            {
                w.Item.Status = "duplicate";
                w.Item.Notes.Add($"Doublon : même artiste et même titre que « {dup.With} » ({where}). Ne sera pas enregistré, sauf si vous forcez.");
                taken.Remove(w.Item.DestFull!);
            }
        }
    }

    private static string StatusOf(PlanItem item)
    {
        return item.Status is "skipped" or "error" or "duplicate" ? item.Status : item.Confidence switch
        {
            "sure" => "ready",
            "guess" => "guess",
            _ => "unknown"
        };
    }

    private static Plan Group(List<Work> works, PlannerOptions o)
    {
        var plan = new Plan();
        var groups = new Dictionary<string, PlanGroup>();

        // Les dossiers d'origine qui contiennent des morceaux sans metadata : leurs annexes (pochette...) les accompagnent.
        var missingDirs = works
            .Where(w => w.Item.Role == "main" && w.Item.Kind == "music" && w.Item.Missing.Count > 0 && !w.Rejected)
            .Select(w => w.ClientDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var w in works)
        {
            var item = w.Item;
            item.Status = StatusOf(item);
            string key;
            if (w.Rejected || item.Dest is null)
            {
                key = "ignored";
            }
            else
            {
                // Un groupe = un dossier de destination (hors saison pour les séries : une série = un groupe).
                var rel = item.Dest.Contains('/') ? item.Dest[..item.Dest.LastIndexOf('/')] : string.Empty;
                if (item.Kind == "series" && rel.Contains('/'))
                {
                    rel = rel[..rel.IndexOf('/')];
                }

                key = $"{item.Kind}|{rel}";
                if (item.Kind == "music" && (item.Missing.Count > 0 || (item.Role == "extra" && missingDirs.Contains(w.ClientDir))))
                {
                    key = "music|?missing";
                }
            }

            item.GroupKey = key;
            if (!groups.TryGetValue(key, out var g))
            {
                g = new PlanGroup { Key = key, Kind = key == "ignored" ? "ignored" : item.Kind ?? "movie" };
                g.Folder = key is "ignored" or "music|?missing" ? null : key[(key.IndexOf('|') + 1)..];
                g.Missing = key == "music|?missing";
                groups[key] = g;
            }

            g.Items.Add(item);
        }

        foreach (var g in groups.Values)
        {
            var lead = g.Items.FirstOrDefault(i => i.Role == "main" && i.Status is not ("skipped" or "error"))
                ?? g.Items.FirstOrDefault(i => i.Status is not ("skipped" or "error"))
                ?? g.Items[0];

            if (g.Missing)
            {
                g.Label = "Musiques sans metadata";
                g.Status = "review";
            }
            else if (g.Kind == "ignored")
            {
                g.Label = "Fichiers ignorés";
                g.Status = "ignored";
            }
            else
            {
                if (string.IsNullOrEmpty(g.Folder))
                {
                    g.Label = $"À la racine : {KindLabel(g.Kind)}";
                }
                else
                {
                    g.Label = g.Kind switch
                    {
                        "music" => string.Join(" — ", new[] { lead.Artist, lead.Album }.Where(s => !string.IsNullOrWhiteSpace(s))),
                        _ => PathBuilder.TitleFolder(lead.Title, lead.Year, g.Folder!)
                    };
                    if (string.IsNullOrWhiteSpace(g.Label))
                    {
                        g.Label = g.Folder!;
                    }
                }

                g.Title = lead.Title;
                g.Year = lead.Year;
                g.Artist = lead.Artist;
                g.Album = lead.Album;
                g.Status = g.Items.Any(i => i.Status is "guess" or "unknown" or "duplicate") ? "review" : "ready";
            }

            g.Items = g.Items
                .OrderBy(i => i.Role switch { "main" => 0, "subtitle" => 1, _ => 2 })
                .ThenBy(i => i.Season ?? int.MaxValue)
                .ThenBy(i => i.Episode ?? int.MaxValue)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Séries et films regroupent aussi leurs sous-titres : on trie sous-titres juste après leur vidéo pour l'affichage.
        foreach (var g in groups.Values.Where(g => g.Kind == "series"))
        {
            g.Items = g.Items
                .OrderBy(i => i.Season ?? int.MaxValue)
                .ThenBy(i => i.Episode ?? int.MaxValue)
                .ThenBy(i => i.Role switch { "main" => 0, "subtitle" => 1, _ => 2 })
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        plan.Groups = groups.Values
            .OrderBy(g => g.Kind == "ignored" ? 1 : 0)
            .ThenBy(g => g.Kind switch { "music" => 0, "movie" => 1, "series" => 2, _ => 3 })
            .ThenBy(g => g.Missing ? 0 : 1)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var all = plan.Groups.SelectMany(g => g.Items).ToList();
        plan.Summary = new PlanSummary
        {
            Files = all.Count,
            Groups = plan.Groups.Count(g => g.Kind != "ignored"),
            Ready = all.Count(i => i.Status == "ready"),
            ToReview = all.Count(i => i.Status is "guess" or "unknown" or "duplicate"),
            Rejected = all.Count(i => i.Status is "skipped" or "error")
        };
        return plan;
    }
}
