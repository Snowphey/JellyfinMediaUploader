using System.Diagnostics;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services;

// Tests du moteur de détection et du planificateur (sans Jellyfin, sans NuGet).
// Lancer : dotnet run   (depuis ce dossier). Code de sortie non nul si un test échoue.
var failures = 0;
var total = 0;

void Check(string name, object? actual, object? expected)
{
    total++;
    var same = actual is Array a && expected is Array b
        ? a.Cast<object?>().SequenceEqual(b.Cast<object?>())
        : Equals(actual, expected);
    if (!same)
    {
        failures++;
        var fmt = (object? x) => x is Array arr ? "[" + string.Join(", ", arr.Cast<object?>()) + "]" : x?.ToString() ?? "null";
        Console.WriteLine($"ÉCHEC  {name}\n         attendu : {fmt(expected)}\n         obtenu  : {fmt(actual)}");
    }
}

void True(string name, bool condition) => Check(name, condition, true);

var engine = new DetectionEngine(DefaultRules.Create());

// ---------- Détection : films et séries ----------

(string? Kind, string? Title, int? Year, int? Season, int? Episode, string Conf) D(string path, string? forced = null)
{
    var d = engine.DetectVideo(path, forced);
    return (d.Kind, d.Title, d.Year, d.Season, d.Episode, d.Confidence);
}

Check("S01E02 torrent", D("Breaking.Bad.S02E05.720p.BluRay.x264-GRP.mkv"), ("series", "Breaking Bad", (int?)null, (int?)2, (int?)5, "sure"));
Check("S01E02 avec année et titre d'épisode", D("Show Name (2019) S01E02 - Le Titre.mkv"), ("series", "Show Name", (int?)2019, (int?)1, (int?)2, "sure"));
Check("1x02", D("Show Name - 1x02 - Titre.mkv"), ("series", "Show Name", (int?)null, (int?)1, (int?)2, "sure"));
Check("Season 1 Episode 2", D("Show Name Season 1 Episode 2.mkv"), ("series", "Show Name", (int?)null, (int?)1, (int?)2, "sure"));
Check("Saison 2 Episode 5", D("Titre Saison 2 Episode 5.mkv"), ("series", "Titre", (int?)null, (int?)2, (int?)5, "sure"));
Check("S00 = spéciaux", D("Show.S00E01.mkv"), ("series", "Show", (int?)null, (int?)0, (int?)1, "sure"));
Check("animé avec saison", D("[Group] Titre S2 - 05 [1080p].mkv"), ("series", "Titre", (int?)null, (int?)2, (int?)5, "sure"));
Check("sous-titre de série", D("Show.S01E02.fr.srt"), ("series", "Show", (int?)null, (int?)1, (int?)2, "sure"));

Check("film Titre (2019)", D("Film (2019).mkv"), ("movie", "Film", (int?)2019, (int?)null, (int?)null, "sure"));
Check("film torrent", D("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv"), ("movie", "The Matrix", (int?)1999, (int?)null, (int?)null, "sure"));
Check("Se7en.1995 n'est pas une série", D("Se7en.1995.mkv"), ("movie", "Se7en", (int?)1995, (int?)null, (int?)null, "sure"));
Check("Movie 1920x1080 n'est pas une série", D("Movie 1920x1080.mkv"), ("movie", (string?)null, (int?)null, (int?)null, (int?)null, "unknown"));
Check("Wonder Woman 1984 (2020)", D("Wonder Woman 1984 (2020).mkv"), ("movie", "Wonder Woman 1984", (int?)2020, (int?)null, (int?)null, "sure"));
Check("2001 A Space Odyssey", D("2001.A.Space.Odyssey.1968.1080p.mkv"), ("movie", "2001 A Space Odyssey", (int?)1968, (int?)null, (int?)null, "sure"));
Check("année future ignorée", D("Movie.2019.2099.mkv"), ("movie", "Movie", (int?)2019, (int?)null, (int?)null, "sure"));
Check("sous-titre de film", D("Film (2019).fr.forced.srt"), ("movie", "Film", (int?)2019, (int?)null, (int?)null, "sure"));
Check("nom inconnu", D("random.mkv"), ("movie", (string?)null, (int?)null, (int?)null, (int?)null, "unknown"));

// Animé sans saison : seulement en mode « série ».
Check("animé sans saison, auto : pas une série", D("[Group] Titre - 05 [1080p].mkv").Kind, "movie");
Check("animé sans saison, forcé", D("[Group] Titre - 05 [1080p].mkv", "series"), ("series", "Titre", (int?)null, (int?)1, (int?)5, "guess"));
Check("animé sans groupe, forcé", D("Titre - 12.mkv", "series"), ("series", "Titre", (int?)null, (int?)1, (int?)12, "guess"));
Check("série forcée mais illisible", D("random.mkv", "series").Conf, "unknown");

// Contexte des dossiers.
Check("dossier + SxxEyy sans titre", D("Breaking Bad (2008)/Season 1/S01E02.mkv"), ("series", "Breaking Bad", (int?)2008, (int?)1, (int?)2, "guess"));
Check("dossier Série/Season N/NN - Titre", D("Show/Season 2/05 - Titre.mkv"), ("series", "Show", (int?)null, (int?)2, (int?)5, "guess"));
Check("dossier Série (2019)/Saison 3/Ep 07", D("Série (2019)/Saison 3/Ep 07.mkv"), ("series", "Série", (int?)2019, (int?)3, (int?)7, "guess"));
Check("film dans un dossier Titre (année)", D("Dune (2021)/video.mkv"), ("movie", "Dune", (int?)2021, (int?)null, (int?)null, "guess"));
Check("le nom prime sur le dossier", D("Dune (2021)/Dune.Part.Two.2024.mkv"), ("movie", "Dune Part Two", (int?)2024, (int?)null, (int?)null, "sure"));

var dbl = engine.DetectVideo("Show.S01E01E02.mkv", null);
True("double épisode signalé", dbl.Notes.Any(n => n.Contains("Double épisode")) && dbl.Episode == 1);

// ---------- Détection : musique ----------

var mTags = engine.DetectMusic("01 - Titre.flac", new TagInfo("Artiste", "Album", "Titre"));
Check("musique : tags", (mTags.Artist, mTags.Album, mTags.Confidence), ("Artiste", "Album", "sure"));
var mPath = engine.DetectMusic("Musique/Daft Punk/Discovery/01 - One More Time.flac", null);
Check("musique : dossiers", (mPath.Artist, mPath.Album, mPath.Confidence), ("Daft Punk", "Discovery", "guess"));
var mFill = engine.DetectMusic("Daft Punk/Discovery/01.flac", new TagInfo(null, "Discovery", null));
Check("musique : tags incomplets complétés", (mFill.Artist, mFill.Album, mFill.Confidence), ("Daft Punk", "Discovery", "guess"));
var mNone = engine.DetectMusic("01.flac", null);
Check("musique : rien", (mNone.HasIdentity, mNone.Confidence), (false, "unknown"));

// ---------- Règles personnalisées, validation, sécurité ----------

Check("regex invalide", DetectionEngine.Validate(new DetectionRule { Kind = "movie", Pattern = "(" })?.StartsWith("Expression invalide"), true);
Check("film sans groupe title", DetectionEngine.Validate(new DetectionRule { Kind = "movie", Pattern = @"\d+" }), "Un film nécessite le groupe nommé (?<title>…).");
Check("série sans groupe episode", DetectionEngine.Validate(new DetectionRule { Kind = "series", Pattern = @"(?<title>.+)" }), "Une série nécessite le groupe nommé (?<episode>…).");
Check("règle valide", DetectionEngine.Validate(new DetectionRule { Kind = "movie", Pattern = @"(?<title>.+)" }), null);
Check("type inconnu", DetectionEngine.Validate(new DetectionRule { Kind = "podcast", Pattern = "x" }) is not null, true);
Check("règles par défaut toutes valides", DefaultRules.Create().Count(r => DetectionEngine.Validate(r) is not null), 0);

var custom = new DetectionEngine(new[]
{
    new DetectionRule { Id = "mine", Name = "Mon format", Kind = "series", Pattern = @"^(?<title>.+?)_ep(?<episode>\d+)_s(?<season>\d+)$" }
}.Concat(DefaultRules.Create()));
var cd = custom.DetectVideo("Ma_Serie_ep12_s3.mkv", null);
Check("règle personnalisée prioritaire", (cd.Title, cd.Season, cd.Episode, cd.RuleName), ("Ma Serie", (int?)3, (int?)12, "Mon format"));

var offRules = DefaultRules.Create();
offRules.First(r => r.Id == "series-sxxexx").Enabled = false;
Check("règle désactivée : plus reconnue par S01E02", new DetectionEngine(offRules).DetectVideo("Breaking.Bad.S02E05.mkv", null).RuleId != "series-sxxexx", true);

var slow = new DetectionEngine(new[] { new DetectionRule { Id = "bad", Name = "Lente", Kind = "movie", Pattern = @"^(?<title>(a+)+)$" } }.Concat(DefaultRules.Create()));
var sw = Stopwatch.StartNew();
var sd = slow.DetectVideo(new string('a', 60) + "!.2019.mkv", null);
sw.Stop();
True("regex pathologique : interrompue en moins de 3 s", sw.ElapsedMilliseconds < 3000);
True("regex pathologique : le reste fonctionne", sd.Kind == "movie");

// ---------- Planificateur ----------

const string Music = "/lib/music", Movies = "/lib/movies", Shows = "/lib/shows";

PlannerOptions Opts(bool shows = true, bool overwrite = false, bool rename = true) => new()
{
    MusicRoot = Music,
    MoviesRoot = Movies,
    ShowsRoot = shows ? Shows : string.Empty,
    Audio = new() { ".mp3", ".flac", ".m4a" },
    Video = new() { ".mkv", ".mp4" },
    Extra = new() { ".jpg", ".png", ".nfo", ".lrc", ".srt", ".ass", ".vtt" },
    Overwrite = overwrite,
    RenameSubtitles = rename
};

List<PlanFile> Files(params string[] paths) => paths.Select((p, i) => new PlanFile { Id = "f" + i, ClientPath = p, Size = 10, Analyzed = true }).ToList();

Plan Run(List<PlanFile> files, string mode = "auto", PlannerOptions? o = null, Dictionary<string, ItemOverride>? ov = null, params string[] existing)
{
    return Planner.Build(files, ov ?? new Dictionary<string, ItemOverride>(), mode, o ?? Opts(), engine, new FakeFs(existing));
}

PlanItem Item(Plan p, string name) => p.Groups.SelectMany(g => g.Items).First(i => i.Name == name);

// Série complète avec sous-titres et pochette.
var show = Run(Files(
    "Show (2019)/Season 1/Show.2019.S01E01.mkv",
    "Show (2019)/Season 1/Show.2019.S01E02.mkv",
    "Show (2019)/Season 1/Show.2019.S01E01.fr.srt",
    "Show (2019)/Season 2/Show.2019.S02E01.mkv",
    "Show (2019)/poster.jpg"));
Check("série : un seul groupe", show.Groups.Count, 1);
Check("série : libellé", show.Groups[0].Label, "Show (2019)");
Check("série : dossier saison 1", Item(show, "Show.2019.S01E02.mkv").Dest, "Show (2019)/Season 01/Show.2019.S01E02.mkv");
Check("série : dossier saison 2", Item(show, "Show.2019.S02E01.mkv").Dest, "Show (2019)/Season 02/Show.2019.S02E01.mkv");
Check("série : sous-titre à côté de sa vidéo", Item(show, "Show.2019.S01E01.fr.srt").Dest, "Show (2019)/Season 01/Show.2019.S01E01.fr.srt");
Check("série : sous-titre déjà bien nommé, pas renommé", Item(show, "Show.2019.S01E01.fr.srt").Pair!.Renamed, false);
Check("série : affiche à la racine de la série", Item(show, "poster.jpg").Dest, "Show (2019)/poster.jpg");
Check("série : tout est sûr", show.Summary.ToReview, 0);

// Sous-titre à renommer.
var ren = Run(Files("Film.2019.1080p.BluRay-GRP.mkv", "Film.2019.fr.srt", "Film.2019.en.forced.srt"));
Check("film : un seul groupe", ren.Groups.Count, 1);
Check("film : sous-titre renommé", Item(ren, "Film.2019.fr.srt").NewName, "Film.2019.1080p.BluRay-GRP.fr.srt");
Check("film : sous-titre forcé renommé", Item(ren, "Film.2019.en.forced.srt").NewName, "Film.2019.1080p.BluRay-GRP.en.forced.srt");
Check("film : même dossier", Item(ren, "Film.2019.fr.srt").Dest, "Film (2019)/Film.2019.1080p.BluRay-GRP.fr.srt");
Check("film : association indiquée", Item(ren, "Film.2019.fr.srt").Pair!.WithName, "Film.2019.1080p.BluRay-GRP.mkv");
Check("film : sûr", Item(ren, "Film.2019.fr.srt").Status, "ready");

// Garder le nom d'origine.
var keep = Run(Files("Film.2019.1080p.mkv", "Film.2019.fr.srt"), ov: new() { ["f1"] = new ItemOverride { KeepName = true } });
Check("garder le nom d'origine", Item(keep, "Film.2019.fr.srt").NewName, "Film.2019.fr.srt");
var noRename = Run(Files("Film.2019.1080p.mkv", "Film.2019.fr.srt"), o: Opts(rename: false));
Check("renommage désactivé", Item(noRename, "Film.2019.fr.srt").NewName, "Film.2019.fr.srt");

// Collision : la vidéo existe déjà, le sous-titre suit le nouveau nom.
var col = Run(Files("Film (2019).mkv", "Film (2019).fr.srt"), existing: Movies + "/Film (2019)/Film (2019).mkv");
Check("collision : vidéo renommée", Item(col, "Film (2019).mkv").NewName, "Film (2019) (2).mkv");
Check("collision : sous-titre suit", Item(col, "Film (2019).fr.srt").NewName, "Film (2019) (2).fr.srt");
Check("collision : à vérifier", Item(col, "Film (2019).mkv").Status, "guess");

// Doublon dans le lot.
var dup = Run(Files("a/Film (2019).mkv", "b/Film (2019).mkv"));
Check("doublon dans le lot", dup.Groups.SelectMany(g => g.Items).Select(i => i.NewName).OrderBy(x => x, StringComparer.Ordinal).ToArray(), new[] { "Film (2019) (2).mkv", "Film (2019).mkv" });

// Sous-titre seul, vidéo déjà en bibliothèque.
var lib = Run(Files("Film.2019.fr.srt"), existing: Movies + "/Film (2019)/Film.2019.1080p.mkv");
Check("bibliothèque : renommé d'après la vidéo existante", Item(lib, "Film.2019.fr.srt").NewName, "Film.2019.1080p.fr.srt");
Check("bibliothèque : source indiquée", Item(lib, "Film.2019.fr.srt").Pair!.Source, "library");
Check("bibliothèque : à confirmer", Item(lib, "Film.2019.fr.srt").Status, "guess");

// Sous-titre orphelin.
var orph = Run(Files("Film (2019).fr.srt"));
Check("orphelin : placé dans le dossier du film", Item(orph, "Film (2019).fr.srt").Dest, "Film (2019)/Film (2019).fr.srt");
True("orphelin : sans association et à vérifier", Item(orph, "Film (2019).fr.srt").Pair is null && Item(orph, "Film (2019).fr.srt").Status == "guess");

// Association manuelle.
var man = Run(Files("Un.Film.2019.mkv", "Autre.Film.2020.mkv", "sous-titres.fr.srt"), ov: new() { ["f2"] = new ItemOverride { PairWith = "f1" } });
Check("manuel : dossier de la vidéo choisie", Item(man, "sous-titres.fr.srt").Dest, "Autre Film (2020)/Autre.Film.2020.fr.srt");
Check("manuel : sûr", Item(man, "sous-titres.fr.srt").Status, "ready");

// Épisodes sans titre dans le nom, sous-titres appariés par dossier.
var folder = Run(Files("Mon Anime/Season 2/S02E03.mkv", "Mon Anime/Season 2/S02E03.fr.srt"));
Check("dossier : épisode", Item(folder, "S02E03.mkv").Dest, "Mon Anime/Season 02/S02E03.mkv");
Check("dossier : sous-titre apparié", Item(folder, "S02E03.fr.srt").Pair?.WithName, "S02E03.mkv");
Check("dossier : titre déduit, à vérifier", folder.Groups[0].Status, "review");

// Série sans dossier configuré.
var noShows = Run(Files("Show.S01E01.mkv"), o: Opts(shows: false));
Check("séries non configurées", Item(noShows, "Show.S01E01.mkv").Status, "error");

// Mode forcé.
var forcedSeries = Run(Files("[Group] Titre - 05 [1080p].mkv"), "series");
Check("animé forcé : dossier", Item(forcedSeries, "[Group] Titre - 05 [1080p].mkv").Dest, "Titre/Season 01/[Group] Titre - 05 [1080p].mkv");
True("animé forcé : à vérifier, saison 1 signalée", Item(forcedSeries, "[Group] Titre - 05 [1080p].mkv").Notes.Any(n => n.Contains("Saison 1 supposée")) && forcedSeries.Summary.ToReview == 1);
var autoAnime = Run(Files("[Group] Titre - 05 [1080p].mkv"));
Check("animé non forcé : pas une série", Item(autoAnime, "[Group] Titre - 05 [1080p].mkv").Kind, "movie");
Check("mode musique : vidéo ignorée", Item(Run(Files("Film (2019).mkv"), "music"), "Film (2019).mkv").Status, "skipped");
Check("mode film : audio ignoré", Item(Run(Files("01.flac"), "movie"), "01.flac").Status, "skipped");
Check("extension refusée", Item(Run(Files("virus.exe")), "virus.exe").Status, "skipped");

// Musique : albums regroupés, pochette qui suit.
var mus = Run(new List<PlanFile>
{
    new() { Id = "a", ClientPath = "Disc/01.flac", Tags = new TagInfo("Artiste", "Album 1", "x"), Analyzed = true },
    new() { Id = "b", ClientPath = "Disc/02.flac", Tags = new TagInfo("Artiste", "Album 1", "y"), Analyzed = true },
    new() { Id = "c", ClientPath = "Autre/01.flac", Tags = new TagInfo("Artiste", "Album 2", "z"), Analyzed = true },
    new() { Id = "d", ClientPath = "Disc/cover.jpg", Analyzed = true },
    new() { Id = "e", ClientPath = "Disc/01.lrc", Analyzed = true }
});
Check("musique : deux albums", mus.Groups.Count, 2);
Check("musique : pochette dans l'album", Item(mus, "cover.jpg").Dest, "Artiste/Album 1/cover.jpg");
Check("musique : paroles avec la piste", Item(mus, "01.lrc").Dest, "Artiste/Album 1/01.lrc");
Check("musique : libellé", mus.Groups[0].Label, "Artiste — Album 1");
Check("musique : rien à vérifier", mus.Summary.ToReview, 0);

// Musique en attente de tags.
var pending = Run(new List<PlanFile> { new() { Id = "p", ClientPath = "01.flac", Analyzed = false } });
True("musique : tags en attente", Item(pending, "01.flac").TagsPending);

// Annexe sans média voisin : racine du type majoritaire, à vérifier.
var lone = Run(Files("poster.jpg", "A/Film (2019).mkv"));
Check("annexe sans voisin : racine films", Item(lone, "poster.jpg").Dest, "poster.jpg");
Check("annexe sans voisin : à vérifier", Item(lone, "poster.jpg").Status, "unknown");
var loose = Run(Files("poster.jpg", "Film (2019).mkv"));
Check("annexe en vrac avec un seul film : rattachée mais à vérifier", (Item(loose, "poster.jpg").Dest, Item(loose, "poster.jpg").Status), ("Film (2019)/poster.jpg", "guess"));
Check("annexe seule sans type : ignorée", Item(Run(Files("poster.jpg")), "poster.jpg").Status, "skipped");

// Corrections de l'utilisateur sur toute une série.
var fixedShow = Run(
    Files("Mauvais.Nom.S01E01.mkv", "Mauvais.Nom.S01E02.mkv"),
    ov: new() { ["f0"] = new ItemOverride { Title = "Bon Nom", Year = 2020 }, ["f1"] = new ItemOverride { Title = "Bon Nom", Year = 2020 } });
Check("correction : un groupe", fixedShow.Groups.Count, 1);
Check("correction : destination", Item(fixedShow, "Mauvais.Nom.S01E02.mkv").Dest, "Bon Nom (2020)/Season 01/Mauvais.Nom.S01E02.mkv");
True("correction : marqué manuel et sûr", Item(fixedShow, "Mauvais.Nom.S01E02.mkv").Manual && Item(fixedShow, "Mauvais.Nom.S01E02.mkv").Status == "ready");
var asMovie = Run(Files("Mauvais.Nom.S01E01.mkv"), ov: new() { ["f0"] = new ItemOverride { Kind = "movie", Title = "Un Film", Year = 2001 } });
Check("correction : épisode redevenu film", Item(asMovie, "Mauvais.Nom.S01E01.mkv").Dest, "Un Film (2001)/Mauvais.Nom.S01E01.mkv");
var noYear = Run(Files("Film (2019).mkv"), ov: new() { ["f0"] = new ItemOverride { Year = 0 } });
Check("correction : année effacée", Item(noYear, "Film (2019).mkv").Dest, "Film/Film (2019).mkv");

// Rangement à plat.
var flatOpts = Opts();
flatOpts.ShowsStructured = false;
Check("rangement à plat", Item(Run(Files("Show.S01E01.mkv"), o: flatOpts), "Show.S01E01.mkv").Dest, "Show.S01E01.mkv");

// Écrasement.
var ow = Run(Files("Film (2019).mkv"), o: Opts(overwrite: true), existing: Movies + "/Film (2019)/Film (2019).mkv");
Check("écrasement : nom conservé", Item(ow, "Film (2019).mkv").NewName, "Film (2019).mkv");
True("écrasement signalé", Item(ow, "Film (2019).mkv").Notes.Any(n => n.Contains("Écrasera")));

// Sécurité : un titre corrigé ne peut pas sortir de la bibliothèque.
var trav = Run(Files("Film (2019).mkv"), ov: new() { ["f0"] = new ItemOverride { Title = "../../etc", Year = 2000 } });
True("traversée de dossier neutralisée", Item(trav, "Film (2019).mkv").DestFull!.StartsWith(Movies + "/"));
True("aucun segment '..' dans la destination", !Item(trav, "Film (2019).mkv").Dest!.Split('/').Contains(".."));

// Doublons de musique : artiste + titre des tags, pas le nom de fichier.
PlanFile Song(string id, string path, string artist, string? title) => new() { Id = id, ClientPath = path, Analyzed = true, Tags = new TagInfo(artist, "Album", title) };
var dupBatch = Run(new List<PlanFile> { Song("a", "01 - Titre.flac", "Artiste", "Mon Titre"), Song("b", "01-Titre (copie).flac", "ARTISTE", "mon  titre!") });
Check("doublon dans le lot (noms de fichiers différents)", (Item(dupBatch, "01 - Titre.flac").Status, Item(dupBatch, "01-Titre (copie).flac").Status), ("ready", "duplicate"));
Check("doublon : jamais enregistré sans forcer", dupBatch.Summary.ToReview, 1);
var dupForce = Run(new List<PlanFile> { Song("a", "x.flac", "Artiste", "Mon Titre"), Song("b", "y.flac", "Artiste", "Mon Titre") }, ov: new() { ["b"] = new ItemOverride { Force = true } });
Check("doublon forcé : enregistré, à vérifier", (Item(dupForce, "y.flac").Status, Item(dupForce, "y.flac").Duplicate!.Forced), ("guess", true));
Check("même titre, autre artiste : pas un doublon", Run(new List<PlanFile> { Song("a", "x.flac", "A", "Titre"), Song("b", "y.flac", "B", "Titre") }).Groups.SelectMany(g => g.Items).Any(i => i.Status == "duplicate"), false);
Check("sans titre dans les tags : pas de détection", Run(new List<PlanFile> { Song("a", "x.flac", "A", null), Song("b", "y.flac", "A", null) }).Summary.ToReview, 0);
var libFs = new FakeFs(new[] { Music + "/Artiste/Autre Album/Ancien nom.flac" }, new() { [Music + "/Artiste/Autre Album/Ancien nom.flac"] = new TagInfo("Artiste", "Autre Album", "Mon titre") });
var dupLib = Planner.Build(new List<PlanFile> { Song("a", "nouveau.flac", "Artiste", "Mon Titre") }, new Dictionary<string, ItemOverride>(), "auto", Opts(), engine, libFs);
Check("doublon avec la bibliothèque (autre album, autre nom)", (Item(dupLib, "nouveau.flac").Status, Item(dupLib, "nouveau.flac").Duplicate!.With), ("duplicate", "Artiste/Autre Album/Ancien nom.flac"));
Check("un doublon d'un autre artiste n'est pas signalé", Planner.Build(new List<PlanFile> { Song("a", "n.flac", "Autre", "Mon Titre") }, new Dictionary<string, ItemOverride>(), "auto", Opts(), engine, libFs).Summary.ToReview, 0);

// Musiques sans metadata : repérées, regroupables, et reclassées une fois renseignées.
PlanFile NoTags(string id, string path) => new() { Id = id, ClientPath = path, Analyzed = true, Tags = null };
var miss = Run(new List<PlanFile> { NoTags("a", "Vrac/01.flac"), NoTags("b", "Vrac/02.flac"), NoTags("c", "Autre/03.flac"), Song("d", "Ok/04.flac", "Vrai", "T"),
    new() { Id = "e", ClientPath = "Demi/05.flac", Analyzed = true, Tags = new TagInfo(null, "Un album", "T5") }, new() { Id = "f", ClientPath = "Vrac/cover.jpg", Analyzed = true } });
var mg = miss.Groups.Single(g => g.Missing);
Check("sans metadata : un groupe réunit les dossiers d'origine", mg.Items.Where(i => i.Role == "main").Select(i => i.Name).OrderBy(x => x).ToArray(), new[] { "01.flac", "02.flac", "03.flac", "05.flac" });
Check("sans metadata : motifs", (string.Join("+", Item(miss, "01.flac").Missing), string.Join("+", Item(miss, "05.flac").Missing)), ("artist+album", "artist"));
True("sans metadata : la pochette du dossier suit", mg.Items.Any(i => i.Name == "cover.jpg"));
True("sans metadata : à vérifier, libellé", mg.Status == "review" && mg.Label == "Musiques sans metadata" && mg.Folder is null);
Check("musique avec tags : hors du groupe", Item(miss, "04.flac").GroupKey, "music|Vrai/Album");
Check("sans metadata : groupe en tête", miss.Groups[0].Missing, true);
// On renseigne deux sous-ensembles différents : ils quittent le groupe et se rangent dans leurs vrais groupes.
var filled = Run(new List<PlanFile> { NoTags("a", "01.flac"), NoTags("b", "02.flac"), NoTags("c", "03.flac"), NoTags("d", "04.flac") },
    ov: new() {
        ["a"] = new ItemOverride { Artist = "Artiste X", Album = "Album 1" }, ["b"] = new ItemOverride { Artist = "Artiste X", Album = "Album 1" },
        ["c"] = new ItemOverride { Artist = "Artiste X", Album = "Album 2" } });
Check("renseigné : deux albums distincts", filled.Groups.Where(g => !g.Missing).Select(g => g.Label).OrderBy(x => x).ToArray(), new[] { "Artiste X — Album 1", "Artiste X — Album 2" });
Check("renseigné : le reste reste sans metadata", filled.Groups.Single(g => g.Missing).Items.Select(i => i.Name).ToArray(), new[] { "04.flac" });
Check("renseigné : plus de motif", Item(filled, "01.flac").Missing.Count, 0);
Check("tags pas encore lus : pas signalé", Run(new List<PlanFile> { new() { Id = "p", ClientPath = "p.flac", Analyzed = false } }).Groups.Any(g => g.Missing), false);
Check("titre seul manquant : pas signalé", Run(new List<PlanFile> { Song("s", "s.flac", "A", null) }).Groups.Any(g => g.Missing), false);
var fbDup = Run(new List<PlanFile> { NoTags("a", "x.flac"), NoTags("b", "y.flac") },
    ov: new() { ["a"] = new ItemOverride { Artist = "Moi", Album = "A", Title = "Chanson" }, ["b"] = new ItemOverride { Artist = "Moi", Album = "A", Title = "chanson" } });
Check("titre saisi : sert aux doublons", Item(fbDup, "y.flac").Status, "duplicate");

// Artistes multiples autour d'un même album.
Check("répétitions retirées", NameTools.DedupeArtists("Alisa Okehazama, Alisa Okehazama, Alisa Okehazama"), "Alisa Okehazama");
Check("noms composés intacts", NameTools.DedupeArtists("Tyler, The Creator"), "Tyler, The Creator");
Check("A, B, A", NameTools.DedupeArtists("A, B, a"), "A, B");
var ost = Run(new List<PlanFile> {
    Song("1", "7 to 3.m4a", "Alisa Okehazama, Alisa Okehazama, Alisa Okehazama", "7 to 3"),
    Song("2", "Impatience.m4a", "Paranom, Kasper, Hiroaki Tsutsumi, Hiroaki Tsutsumi, Hiroaki Tsutsumi", "Impatience"),
    Song("3", "Monoco.mp3", "Lorien Testard", "Monoco"), Song("4", "Alicia.mp3", "Lorien Testard, Alice Duport-Percier", "Alicia") });
Check("même album : un seul groupe", ost.Groups.Count(g => g.Kind == "music"), 1);
Check("artistes disjoints : Various Artists, à vérifier", (Item(ost, "Impatience.m4a").Dest, Item(ost, "Impatience.m4a").Status), ("Various Artists/Album/Impatience.m4a", "guess"));
var proj = Run(new List<PlanFile> {
    new() { Id = "1", ClientPath = "Monoco.mp3", Analyzed = true, Tags = new TagInfo("Lorien Testard", "Clair Obscur", "Monoco") },
    new() { Id = "2", ClientPath = "Alicia.mp3", Analyzed = true, Tags = new TagInfo("Lorien Testard, Alice Duport-Percier", "Clair Obscur: Expedition 33 (OST)", "Alicia") },
    new() { Id = "3", ClientPath = "Autre.mp3", Analyzed = true, Tags = new TagInfo("Lorien Testard, Alice Duport-Percier", "Clair Obscur", "Autre") } });
Check("préfixe commun : le plus court", (Item(proj, "Autre.mp3").Dest, Item(proj, "Autre.mp3").Status), ("Lorien Testard/Clair Obscur/Autre.mp3", "ready"));
Check("autre album non touché", Item(proj, "Alicia.mp3").Dest, "Lorien Testard, Alice Duport-Percier/Clair Obscur: Expedition 33 (OST)/Alicia.mp3");
var lead = Run(new List<PlanFile> { Song("1", "a.flac", "Duo, X", "A"), Song("2", "b.flac", "Duo, Y", "B") }, ov: null);
Check("même premier nom : unifié", (Item(lead, "a.flac").Dest, Item(lead, "b.flac").Dest), ("Duo/Album/a.flac", "Duo/Album/b.flac"));
var keepOv = Run(new List<PlanFile> { Song("1", "a.flac", "P", "A"), Song("2", "b.flac", "Q", "B") }, ov: new() { ["1"] = new ItemOverride { Artist = "Moi" } });
Check("artiste corrigé à la main : respecté", Item(keepOv, "a.flac").Dest, "Moi/Album/a.flac");

// Featurings sur un album de rap.
var rap = Run(new List<PlanFile> { Song("1", "1.flac", "Kendrick Lamar", "A"), Song("2", "2.flac", "Kendrick Lamar, SZA", "B"), Song("3", "3.flac", "Kendrick Lamar feat. Drake", "C"), Song("4", "4.flac", "Kendrick Lamar & Rihanna", "D") });
Check("rap : featurings, un seul dossier, sûr", (rap.Groups.Count(g => g.Kind == "music"), rap.Summary.ToReview, Item(rap, "3.flac").Dest), (1, 0, "Kendrick Lamar/Album/3.flac"));
var rap2 = Run(new List<PlanFile> { Song("1", "1.flac", "Drake", "A"), Song("2", "2.flac", "Future, Drake", "B"), Song("3", "3.flac", "Drake, Future", "C") });
Check("rap : ordre inversé, nom commun, à vérifier", (Item(rap2, "2.flac").Dest, Item(rap2, "2.flac").Status), ("Drake/Album/2.flac", "guess"));
var rap3 = Run(new List<PlanFile> { Song("1", "1.flac", "Duo, X", "A"), Song("2", "2.flac", "Duo, Y", "B") });
Check("même artiste principal, invités différents : sûr", (Item(rap3, "1.flac").Dest, Item(rap3, "1.flac").Status), ("Duo/Album/1.flac", "ready"));

// Choix incertain : les possibilités sont proposées ; le choix de l'utilisateur lève le doute.
Check("choix incertain : propositions", string.Join(" | ", Item(rap2, "2.flac").ArtistChoices), "Drake | Future, Drake | Drake, Future | Various Artists");
Check("choix sûr : pas de proposition", Item(rap, "3.flac").ArtistChoices.Count, 0);
var picked = Run(new List<PlanFile> { Song("1", "1.flac", "Drake", "A"), Song("2", "2.flac", "Future, Drake", "B"), Song("3", "3.flac", "Drake, Future", "C") },
    ov: new() { ["1"] = new ItemOverride { Artist = "Drake" }, ["2"] = new ItemOverride { Artist = "Drake" }, ["3"] = new ItemOverride { Artist = "Drake" } });
Check("choix confirmé : sûr, plus de proposition", (picked.Summary.ToReview, Item(picked, "2.flac").ArtistChoices.Count), (0, 0));

Console.WriteLine(failures == 0 ? $"OK : {total} vérifications." : $"{failures} échec(s) sur {total}.");
return failures == 0 ? 0 : 1;

sealed class FakeFs : IFileProbe
{
    private readonly HashSet<string> _files;
    private readonly Dictionary<string, TagInfo> _tags;

    public FakeFs(IEnumerable<string> files, Dictionary<string, TagInfo>? tags = null)
    {
        _files = new HashSet<string>(files);
        _tags = tags ?? new();
    }

    public IEnumerable<string> ListFilesDeep(string directory) => _files.Where(f => f.StartsWith(directory + "/"));

    public TagInfo? ReadTags(string path) => _tags.TryGetValue(path, out var t) ? t : null;

    public bool Exists(string path) => _files.Contains(path);

    public IEnumerable<string> ListFiles(string directory) => _files.Where(f => Path.GetDirectoryName(f) == directory);
}
