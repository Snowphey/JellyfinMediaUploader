using System.Diagnostics;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services;
using Jellyfin.Plugin.MediaUploader.Services.Import;

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


// ---------- Import : liens ----------

string? Link(string? input) { var l = ImportUrl.Parse(input, out _); return l is null ? null : $"{l.Source}|{l.Kind}|{l.Url}"; }
const string SpId = "37i9dQZF1DXcBWIGoYBM5M";

Check("Spotify playlist", Link($"https://open.spotify.com/playlist/{SpId}?si=abc"), $"Spotify|playlist|https://open.spotify.com/playlist/{SpId}");
Check("Spotify intl + album", Link($"https://open.spotify.com/intl-fr/album/{SpId}"), $"Spotify|album|https://open.spotify.com/album/{SpId}");
Check("Spotify URI", Link($"spotify:track:{SpId}"), $"Spotify|track|https://open.spotify.com/track/{SpId}");
Check("Spotify id invalide", Link("https://open.spotify.com/playlist/abc"), null);
Check("YouTube playlist", Link("https://www.youtube.com/playlist?list=PLabcdefghij12345"), "YouTube|playlist|https://www.youtube.com/playlist?list=PLabcdefghij12345");
Check("YouTube watch + list = playlist", Link("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLabcdefghij12345&index=2"), "YouTube|playlist|https://www.youtube.com/playlist?list=PLabcdefghij12345");
Check("YouTube vidéo", Link("https://youtu.be/dQw4w9WgXcQ?t=3"), "YouTube|track|https://www.youtube.com/watch?v=dQw4w9WgXcQ");
Check("YT Music album OLAK", Link("https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijklmnop"), "YouTube|album|https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijklmnop");
Check("YT Music browse MPRE", Link("https://music.youtube.com/browse/MPREb_gTAcphH99wE"), "YouTube|album|https://music.youtube.com/browse/MPREb_gTAcphH99wE");
Check("mix YouTube refusé", Link("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ"), null);
Check("autre site refusé", Link("https://example.com/watch?v=dQw4w9WgXcQ"), null);
Check("réseau local refusé", Link("http://192.168.1.10:8096/playlist?list=PLabcdefghij12345"), null);
Check("hôte piégé refusé", Link("https://youtube.com.evil.test/playlist?list=PLabcdefghij12345"), null);
Check("option injectée refusée", Link("--exec rm"), null);
Check("fichier refusé", Link("file:///etc/passwd"), null);

// ---------- Import : noms ----------

Check("nettoyage (Official Video)", TrackNaming.CleanTitle("Song Name (Official Video) [HD]"), "Song Name");
Check("nettoyage garde (Remix)", TrackNaming.CleanTitle("Song (Remix)"), "Song (Remix)");
Check("Topic -> artiste", TrackNaming.ArtistFromChannel("Daft Punk - Topic"), "Daft Punk");
Check("VEVO -> artiste", TrackNaming.ArtistFromChannel("AdeleVEVO"), "Adele");
Check("Artiste - Titre", TrackNaming.FromVideo("Daft Punk - One More Time (Official Audio)", "Some Channel"), ("Daft Punk", "One More Time"));
Check("chaîne Topic : pas de découpe", TrackNaming.FromVideo("One More Time", "Daft Punk - Topic"), ("Daft Punk", "One More Time"));
Check("nom de fichier piste", TrackNaming.FileName(1, 5, "Intro", ".m4a"), "05 - Intro.m4a");
Check("nom de fichier disque 2", TrackNaming.FileName(2, 5, "Intro", ".m4a"), "2-05 - Intro.m4a");
Check("nom de fichier sans piste", TrackNaming.FileName(null, null, "A/B", ".mp3"), "A B.mp3");

var specA = new TrackSpec { Id = "t1", Title = "Song", Artist = "Artist", AlbumArtist = "Band", Album = "Record", TrackNo = 3, DiscNo = 1, Year = 2020 };
var specNoAlbum = new TrackSpec { Id = "t2", Title = "Vid", Artist = "Someone" };
Check("rangement source", ImportNaming.Resolve(specA, "List", "source", 9), new TrackMeta("Artist", "Band", "Record", 3, 1, 2020));
Check("rangement playlist", ImportNaming.Resolve(specA, "List", "playlist", 9), new TrackMeta("Artist", "Various Artists", "List", 9, null, 2020));
Check("sans album -> album de la liste", ImportNaming.Resolve(specNoAlbum, "List", "source", 4), new TrackMeta("Someone", "Various Artists", "List", 4, null, null));
Check("chemin client", ImportNaming.ClientPath(ImportNaming.Resolve(specA, "List", "source", 1), "Song", ".m4a"), "Band/Record/03 - Song.m4a");
Check("layout normalisé", ImportNaming.NormalizeLayout("PLAYLIST"), "playlist");

// ---------- Import : yt-dlp ----------

var ytLink = ImportUrl.Parse("https://music.youtube.com/playlist?list=PLabcdefghij12345", out _)!;
var flat = """
{"_type":"playlist","title":"Road Trip","entries":[
 {"id":"dQw4w9WgXcQ","title":"Rick Astley - Never Gonna Give You Up (Official Video)","channel":"Rick Astley","duration":213.0},
 {"id":"abcdefghijk","title":"One More Time","channel":"Daft Punk - Topic","duration":320},
 {"id":"zzzzzzzzzzz","title":"[Private video]"},
 {"id":"not-valid","title":"Bad id","channel":"x"},
 null
]}
""";
var yl = YtDlpClient.ParseListing(flat, ytLink);
Check("yt-dlp : titre de playlist", yl.Title, "Road Trip");
Check("yt-dlp : nombre de morceaux", yl.Tracks.Count, 2);
Check("yt-dlp : artiste/titre découpés", (yl.Tracks[0].Artist, yl.Tracks[0].Title), ("Rick Astley", "Never Gonna Give You Up"));
Check("yt-dlp : chaîne Topic", (yl.Tracks[1].Artist, yl.Tracks[1].Title, yl.Tracks[1].DurationSec), ("Daft Punk", "One More Time", (int?)320));
Check("yt-dlp : adresse reconstruite", yl.Tracks[0].VideoUrl, "https://music.youtube.com/watch?v=dQw4w9WgXcQ");
var albumLink = ImportUrl.Parse("https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijklmnop", out _)!;
var al = YtDlpClient.ParseListing("""{"_type":"playlist","title":"Album - Discovery (14 Songs)","thumbnails":[{"url":"https://i.ytimg.com/a.jpg"},{"url":"https://lh3.googleusercontent.com/b"}],"entries":[{"id":"aaaaaaaaaaa","title":"One More Time","channel":"Daft Punk - Topic"},{"id":"bbbbbbbbbbb","title":"Aerodynamic","channel":"Daft Punk - Topic"}]}""", albumLink);
Check("yt-dlp album : titre", al.Title, "Discovery");
Check("yt-dlp album : genre", al.Kind, "album");
Check("yt-dlp album : album/numéros", (al.Tracks[1].Album, al.Tracks[1].AlbumArtist, al.Tracks[1].TrackNo), ("Discovery", "Daft Punk", (int?)2));
Check("yt-dlp album : pochette", al.CoverUrl, "https://lh3.googleusercontent.com/b");
var single = YtDlpClient.ParseListing("""{"id":"dQw4w9WgXcQ","title":"Song","track":"Real Title","artist":"Real Artist","channel":"x"}""", ImportUrl.Parse("https://youtu.be/dQw4w9WgXcQ", out _)!);
Check("yt-dlp vidéo seule : métadonnées musique", (single.Kind, single.Tracks[0].Artist, single.Tracks[0].Title), ("track", "Real Artist", "Real Title"));

var ctx = new ToolContext { ToolsDir = "/tmp/mu-tools", YtDlp = "/bin/yt-dlp", Ffmpeg = "/usr/bin/ffmpeg", Format = "m4a" };
var dl = YtDlpClient.DownloadArgs(ctx, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "/tmp/w");
True("yt-dlp : URL après « -- »", dl[^2] == "--" && dl[^1].EndsWith("dQw4w9WgXcQ"));
True("yt-dlp : sélecteur m4a", dl.Contains("bestaudio[ext=m4a]/bestaudio/best"));
True("yt-dlp : ffmpeg transmis", dl.Contains("--ffmpeg-location") && dl.Contains("/usr/bin/ffmpeg"));
True("yt-dlp : pas de shell, un argument par élément", dl.All(a => !a.Contains("&&")));
var ls = YtDlpClient.ListArgs(new ToolContext { Deno = "/x/deno", CookiesPath = "/nonexistent" }, "https://www.youtube.com/playlist?list=PLabcdefghij12345");
True("yt-dlp : liste à plat + deno", ls.Contains("--flat-playlist") && ls.Contains("deno:/x/deno") && !ls.Contains("--cookies"));

// ---------- Import : Spotify (page d'intégration) ----------

var spLink = ImportUrl.Parse($"https://open.spotify.com/playlist/{SpId}", out _)!;
string Embed(string entity) => "<html><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":" + entity + "}}}}}</script></html>";
var sl = SpotifyClient.ParseEmbed(Embed("""{"type":"playlist","title":"Road Trip","coverArt":{"sources":[{"url":"https://i.scdn.co/image/x"}]},"trackList":[{"title":"Get Lucky","subtitle":"Daft Punk, Pharrell Williams","duration":369000,"entityType":"track"},{"title":"Lose Yourself","subtitle":"Eminem","duration":326400,"entityType":"track"},{"title":"Épisode","subtitle":"Podcast","entityType":"episode"}]}"""), spLink);
Check("Spotify : titre de playlist", sl.Title, "Road Trip");
Check("Spotify : morceaux (épisodes écartés)", sl.Tracks.Count, 2);
Check("Spotify : artistes", sl.Tracks[0].Artist, "Daft Punk, Pharrell Williams");
Check("Spotify : durée en secondes", sl.Tracks[1].DurationSec, (int?)326);
Check("Spotify : pochette", sl.CoverUrl, "https://i.scdn.co/image/x");
Check("Spotify : pas de remarque sous 100 titres", sl.Note, null);
var big = SpotifyClient.ParseEmbed(Embed("{\"type\":\"playlist\",\"title\":\"Big\",\"trackList\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"title\":\"T{i}\",\"subtitle\":\"A\",\"entityType\":\"track\"}}")) + "]}"), spLink);
True("Spotify : remarque à 100 titres", big.Tracks.Count == 100 && big.Note is not null);
var spAlbum = SpotifyClient.ParseEmbed(Embed("""{"type":"album","title":"Discovery","subtitle":"Daft Punk","trackList":[{"title":"One More Time","subtitle":"Daft Punk","duration":320000,"entityType":"track"}]}"""), ImportUrl.Parse($"https://open.spotify.com/album/{SpId}", out _)!);
Check("Spotify album : album et numéro", (spAlbum.Tracks[0].Album, spAlbum.Tracks[0].AlbumArtist, spAlbum.Tracks[0].TrackNo), ("Discovery", "Daft Punk", (int?)1));
var spTrack = SpotifyClient.ParseEmbed(Embed("""{"type":"track","title":"Never Gonna Give You Up","artists":[{"name":"Rick Astley"}],"duration":213573}"""), ImportUrl.Parse($"https://open.spotify.com/track/{SpId}", out _)!);
Check("Spotify titre seul", (spTrack.Tracks[0].Artist, spTrack.Tracks[0].Title, spTrack.Tracks[0].DurationSec), ("Rick Astley", "Never Gonna Give You Up", (int?)214));
var search = YtDlpClient.SearchArgs(new ToolContext { Format = "m4a" }, sl.Tracks[0], "/w", strict: true);
True("recherche : requête après « -- »", search[^2] == "--" && search[^1] == "ytsearch5:Daft Punk, Pharrell Williams Get Lucky");
True("recherche : filtre de durée ±15 s", search[search.IndexOf("--match-filters") + 1] == "duration>=354 & duration<=384");
True("recherche : un seul téléchargement", search[search.IndexOf("--max-downloads") + 1] == "1");
True("recherche souple : sans filtre", !YtDlpClient.SearchArgs(new ToolContext { Format = "m4a" }, sl.Tracks[0], "/w", strict: false).Contains("--match-filters"));

// ---------- Import : MusicBrainz ----------

var artists = MusicBrainzClient.ParseArtists("""{"artists":[{"id":"056e4f3e-d505-4dad-8ec1-d04f521cbb56","name":"Daft Punk","disambiguation":"French electronic duo","country":"FR","type":"Group","score":100},{"name":"sans id"}]}""");
Check("MB : artistes", artists.Count, 1);
Check("MB : artiste", (artists[0].Name, artists[0].Country), ("Daft Punk", "FR"));
var (groups, groupTotal) = MusicBrainzClient.ParseReleaseGroups("""{"release-group-count":2,"release-groups":[{"id":"11111111-1111-1111-1111-111111111111","title":"Discovery","primary-type":"Album","secondary-types":[],"first-release-date":"2001-03-12"},{"id":"22222222-2222-2222-2222-222222222222","title":"Alive 2007","primary-type":"Album","secondary-types":["Live"],"first-release-date":"2007-11-19"}]}""");
Check("MB : albums", (groups.Count, groupTotal), (2, 2));
Check("MB : type secondaire", groups[1].Extras.ToArray(), new[] { "Live" });
Check("MB : échappement Lucene", MusicBrainzClient.EscapeLucene("AC/DC (live)"), "AC\\/DC \\(live\\)");

string Rel(string title, string date, int tracks) => "{\"title\":\"" + title + "\",\"date\":\"" + date + "\",\"artist-credit\":[{\"name\":\"Daft Punk\",\"joinphrase\":\"\",\"artist\":{\"name\":\"Daft Punk\"}}],\"media\":[{\"position\":1,\"tracks\":["
    + string.Join(",", Enumerable.Range(1, tracks).Select(i => $"{{\"position\":{i},\"title\":\"T{i}\",\"length\":{200000 + i * 1000},\"recording\":{{\"id\":\"00000000-0000-0000-0000-00000000000{i % 10}\",\"isrcs\":[\"ISRC{i}\"]}},\"artist-credit\":[{{\"name\":\"Daft Punk\",\"joinphrase\":\" feat. \",\"artist\":{{\"name\":\"Daft Punk\"}}}},{{\"name\":\"Guest\",\"joinphrase\":\"\",\"artist\":{{\"name\":\"Guest\"}}}}]}}")) + "]}]}";
var releases = "{\"releases\":[" + Rel("Discovery (Deluxe)", "2012-01-01", 16) + "," + Rel("Discovery", "2001-03-12", 14) + "," + Rel("Discovery", "2001-03-13", 14) + "]}";
var mb = MusicBrainzClient.ParseRelease(releases, "11111111-1111-1111-1111-111111111111", "https://coverartarchive.org/release-group/x/front-500");
Check("MB : sortie la plus représentative (14 pistes, la plus ancienne)", mb.Tracks.Count, 14);
Check("MB : album", (mb.Title, mb.Tracks[0].Year), ("Discovery", (int?)2001));
Check("MB : artiste crédité", mb.Tracks[0].Artist, "Daft Punk feat. Guest");
Check("MB : durée", mb.Tracks[1].DurationSec, (int?)202);
Check("cause yt-dlp préférée au message générique", ProcessRunner.Summarize("AudioProviderError: YT-DLP download error -\nERROR: [youtube] lYWldRgI9fo: Sign in to confirm your age\nAudioProviderError: YT-DLP download error -"), "ERROR: [youtube] lYWldRgI9fo: Sign in to confirm your age");
True("environnement sans retour à la ligne", new ToolContext { ToolsDir = "/tmp/mu-t" }.ChildEnvironment()["COLUMNS"] == "400");
True("nouvelle tentative détaillée", new ToolContext { Format = "m4a" }.WithVerbose().Verbose);
Check("plantage PyInstaller : cause affichée", ProcessRunner.Summarize("Traceback (most recent call last):\n  File \"x.py\", line 1\nKeyError: 'name'\n[PYI-1430:ERROR] Failed to execute script '__main__' due to unhandled exception!"), "KeyError: 'name'");
Check("MB : aucune sortie", MusicBrainzClient.ParseRelease("{\"releases\":[]}", "x", null).Tracks.Count, 0);

var longName = "[Group] Show - 05 " + new string('x', 200) + " [1080p].mkv";
var safeLong = PathBuilder.SanitizeFileName(longName, "file");
True("nom long : extension conservée", safeLong.EndsWith(".mkv") && safeLong.Length <= 150);
var cjk = PathBuilder.SanitizeFileName(new string('\u3042', 200) + ".mp3", "file");
True("nom long : limite en octets (255)", cjk.EndsWith(".mp3") && System.Text.Encoding.UTF8.GetByteCount(cjk) <= 255);
Check("nom court inchangé", PathBuilder.SanitizeFileName("Film (2019).mkv", "file"), "Film (2019).mkv");
Check("nom vide : repli", PathBuilder.SanitizeFileName(".mkv", "file"), "file.mkv");
True("nom : séparateurs retirés", !PathBuilder.SanitizeFileName("../../etc/passwd.mkv", "file").Contains('/'));

// ---------- Import : cadence anti-robot ----------

var now0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
var clockNow = now0;
ImportThrottle.Reset();
ImportThrottle.Clock = () => clockNow;
ImportThrottle.Pick = (min, max) => max;
var tcx = new ToolContext { MinDelaySeconds = 8, MaxDelaySeconds = 25, MaxPerHour = 3, MaxPerDay = 5 };
Check("cadence : premier téléchargement immédiat", ImportThrottle.TryReserve(tcx), TimeSpan.Zero);
Check("cadence : délai avant le suivant", ImportThrottle.TryReserve(tcx), TimeSpan.FromSeconds(25));
clockNow = now0.AddSeconds(25);
Check("cadence : suivant autorisé", ImportThrottle.TryReserve(tcx), TimeSpan.Zero);
clockNow = now0.AddSeconds(60);
Check("cadence : troisième", ImportThrottle.TryReserve(tcx), TimeSpan.Zero);
clockNow = now0.AddSeconds(100);
Check("cadence : plafond horaire atteint", ImportThrottle.TryReserve(tcx), TimeSpan.FromHours(1) - TimeSpan.FromSeconds(100));
clockNow = now0.AddHours(1).AddSeconds(1);
Check("cadence : plafond horaire libéré", ImportThrottle.TryReserve(tcx), TimeSpan.Zero);
var first = ImportThrottle.ReportBlocked();
Check("cadence : première suspension", first, TimeSpan.FromMinutes(30));
Check("cadence : même incident, pas d'escalade", ImportThrottle.ReportBlocked(), TimeSpan.FromMinutes(30));
clockNow = clockNow.AddMinutes(31);
var second = ImportThrottle.ReportBlocked();
Check("cadence : récidive, suspension doublée", second, TimeSpan.FromHours(1));
True("cadence : suspendu", ImportThrottle.PausedUntil() is not null && ImportThrottle.TryReserve(tcx) > TimeSpan.FromMinutes(59));
clockNow = clockNow.AddHours(2);
ImportThrottle.ReportSuccess();
Check("cadence : retour à la durée de base", ImportThrottle.ReportBlocked(), TimeSpan.FromMinutes(30));
ImportThrottle.Reset();
ImportThrottle.Clock = () => DateTime.UtcNow;
ImportThrottle.Pick = (min, max) => min + (Random.Shared.NextDouble() * (max - min));
True("blocage : anti-robot", ImportThrottle.IsBlock("ERROR: [youtube] x: Sign in to confirm you’re not a bot"));
True("blocage : 429", ImportThrottle.IsBlock("ERROR: unable to download: HTTP Error 429: Too Many Requests"));
True("blocage : limitation", ImportThrottle.IsBlock("This content isn't available, try again later. The current session has been rate-limited by YouTube"));
True("pas un blocage : âge", !ImportThrottle.IsBlock("ERROR: [youtube] x: Sign in to confirm your age"));
True("pas un blocage : vidéo indisponible", !ImportThrottle.IsBlock("ERROR: Video unavailable"));
True("versions : à jour", !ToolInstaller.IsOutdated("2026.08.19", "2026.08.19") && !ToolInstaller.IsOutdated("v2.1.0", "2.1.0"));
True("versions : mise à jour", ToolInstaller.IsOutdated("2026.07.01", "2026.08.19"));
True("versions : inconnue", !ToolInstaller.IsOutdated(null, "2026.08.19"));
True("recherche : pause entre les requêtes", YtDlpClient.DownloadArgs(new ToolContext(), "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "/w").Contains("--sleep-requests"));

var toolDir = Path.Combine(Path.GetTempPath(), "mu-tools-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(toolDir, "yt-dlp-dist"));
var distExe = Path.Combine(toolDir, "yt-dlp-dist", ToolLocator.FileName("yt-dlp"));
File.WriteAllText(distExe, "x");
Check("outil : version « dossier » trouvée", ToolLocator.Find("yt-dlp", null, toolDir), distExe);
ToolManifest.Record(toolDir, "yt-dlp", "2026.08.19");
Check("outil : version lue sans lancer l'outil", ToolManifest.Known(toolDir, "yt-dlp", distExe), "2026.08.19");
Check("outil : autre exemplaire ignoré", ToolManifest.Known(toolDir, "yt-dlp", "/usr/bin/yt-dlp"), null);
Directory.Delete(toolDir, recursive: true);

True("pochette : Spotify acceptée", CoverHosts.Allowed("https://i.scdn.co/image/abc", out _));
True("pochette : Cover Art Archive acceptée", CoverHosts.Allowed("https://coverartarchive.org/release-group/x/front-500", out _) && CoverHosts.Allowed("https://ia800000.us.archive.org/x.jpg", out _));
True("pochette : autre hôte refusé", !CoverHosts.Allowed("https://evil.example/x.jpg", out _) && !CoverHosts.Allowed("http://i.scdn.co/x.jpg", out _));
var ytm = YtDlpClient.ParseListing("""{"_type":"playlist","title":"Mix","entries":[{"id":"aaaaaaaaaaa","title":"A","channel":"X - Topic","thumbnails":[{"url":"https://i.ytimg.com/vi/aaaaaaaaaaa/hq.jpg"},{"url":"https://lh3.googleusercontent.com/art=w544"}]},{"id":"bbbbbbbbbbb","title":"B","thumbnails":[{"url":"https://i.ytimg.com/vi/bbbbbbbbbbb/hq.jpg"}]}]}""", ytLink);
Check("pochette YouTube Music : miniature carrée seulement", (ytm.Tracks[0].CoverUrl, ytm.Tracks[1].CoverUrl), ("https://lh3.googleusercontent.com/art=w544", (string?)null));

var mbCount = MusicBrainzClient.ParseTrackCount("{\"releases\":[{\"media\":[{\"track-count\":16}]},{\"media\":[{\"track-count\":14}]},{\"media\":[{\"track-count\":14}]},{\"media\":[{\"track-count\":7},{\"track-count\":7}]}]}");
Check("MB : nombre de pistes (le plus courant, tous supports)", mbCount, 14);
Check("MB : égalité -> la plus courte édition", MusicBrainzClient.ParseTrackCount("{\"releases\":[{\"media\":[{\"track-count\":16}]},{\"media\":[{\"track-count\":12}]}]}"), 12);
Check("MB : aucune sortie", MusicBrainzClient.ParseTrackCount("{\"releases\":[]}"), 0);

var qNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
ImportThrottle.Reset();
ImportThrottle.Clock = () => qNow;
ImportThrottle.Pick = (min, max) => min;
var qc = new ToolContext { MinDelaySeconds = 10, MaxDelaySeconds = 10, MaxPerHour = 5, MaxPerDay = 100 };
var q0 = ImportThrottle.Snapshot(qc, 0);
Check("quota : état vide", (q0.UsedHour, q0.UsedDay, q0.NextSlotIn), (0, 0, (TimeSpan?)null));
var q3 = ImportThrottle.Snapshot(qc, 3);
Check("quota : 3 morceaux rentrent (démarrages espacés de 10 s)", (q3.Delayed, q3.LastStartIn), (0, TimeSpan.FromSeconds(20)));
var q8 = ImportThrottle.Snapshot(qc, 8);
Check("quota : 3 morceaux de trop attendent le plafond horaire", q8.Delayed, 3);
True("quota : le dernier démarre après l'heure", q8.LastStartIn > TimeSpan.FromHours(1));
for (var k = 0; k < 5; k++) { ImportThrottle.TryReserve(qc); qNow = qNow.AddSeconds(11); }
var qFull = ImportThrottle.Snapshot(qc, 0);
Check("quota : 5 utilisés cette heure", qFull.UsedHour, 5);
True("quota : prochaine place annoncée", qFull.NextSlotIn is { } w && w > TimeSpan.FromMinutes(50));
var qFree = ImportThrottle.Snapshot(new ToolContext { MaxPerHour = 0, MaxPerDay = 0, MinDelaySeconds = 10, MaxDelaySeconds = 10 }, 500);
Check("quota : sans plafond, rien n'est retardé", qFree.Delayed, 0);
ImportThrottle.Reset();
ImportThrottle.Clock = () => DateTime.UtcNow;
ImportThrottle.Pick = (min, max) => min + (Random.Shared.NextDouble() * (max - min));

Check("yt-dlp : progression", YtDlpClient.PhaseOf("[download]  45.3% of 3.20MiB at 1.20MiB/s ETA 00:02"), ((string, int?)?)("downloading", 45));
Check("yt-dlp : début de téléchargement", YtDlpClient.PhaseOf("[download] Destination: x.webm"), ((string, int?)?)("downloading", 0));
Check("yt-dlp : conversion", YtDlpClient.PhaseOf("[ExtractAudio] Destination: x.m4a"), ((string, int?)?)("converting", null));
Check("yt-dlp : recherche", YtDlpClient.PhaseOf("[youtube] Extracting URL: ytsearch5:Daft Punk"), ((string, int?)?)("searching", null));
Check("yt-dlp : ligne sans information", YtDlpClient.PhaseOf("[info] Available formats"), ((string, int?)?)null);
var qPace = ImportThrottle.Snapshot(new ToolContext(), 0);
Check("quota : pas de délai en attente au repos", qPace.PaceIn, (TimeSpan?)null);

var quotaDir = Path.Combine(Path.GetTempPath(), "mu-quota-" + Guid.NewGuid().ToString("N"));
var pc = new ToolContext { ToolsDir = quotaDir, MinDelaySeconds = 10, MaxDelaySeconds = 10, MaxPerHour = 50, MaxPerDay = 100 };
var pNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
ImportThrottle.Reset();
ImportThrottle.Clock = () => pNow;
ImportThrottle.Pick = (min, max) => min;
ImportThrottle.TryReserve(pc);
pNow = pNow.AddSeconds(11);
ImportThrottle.TryReserve(pc);
ImportThrottle.ReportBlocked();
True("quota persistant : fichier écrit", File.Exists(Path.Combine(quotaDir, "quota.json")));
ImportThrottle.Reset();
var afterRestart = ImportThrottle.Snapshot(pc, 0);
Check("quota persistant : compteurs relus après un redémarrage", (afterRestart.UsedHour, afterRestart.UsedDay), (2, 2));
True("quota persistant : suspension relue", afterRestart.PausedFor is { } pf && pf > TimeSpan.FromMinutes(29));
File.WriteAllText(Path.Combine(quotaDir, "quota.json"), "pas du json");
ImportThrottle.Reset();
Check("quota persistant : fichier abîmé ignoré", ImportThrottle.Snapshot(pc, 0).UsedHour, 0);
pNow = pNow.AddDays(2);
File.WriteAllText(Path.Combine(quotaDir, "quota.json"), "{\"Starts\":[\"2026-01-01T12:00:00Z\"],\"Blocks\":0}");
ImportThrottle.Reset();
Check("quota persistant : démarrages vieux de plus de 24 h oubliés", ImportThrottle.Snapshot(pc, 0).UsedDay, 0);
Directory.Delete(quotaDir, recursive: true);
ImportThrottle.Reset();
ImportThrottle.Clock = () => DateTime.UtcNow;
ImportThrottle.Pick = (min, max) => min + (Random.Shared.NextDouble() * (max - min));

// ---------- Import : outils ----------

Check("asset yt-dlp linux", ToolInstaller.YtDlpAsset(ToolPlatform.LinuxX64), "yt-dlp_linux.zip");
Check("asset yt-dlp arm", ToolInstaller.YtDlpAsset(ToolPlatform.LinuxArm64), "yt-dlp_linux_aarch64.zip");
Check("asset deno", ToolInstaller.DenoAsset(ToolPlatform.LinuxX64), "deno-x86_64-unknown-linux-gnu.zip");
Check("format normalisé", ToolContext.NormalizeFormat("FLAC"), "m4a");
Check("format opus", ToolContext.NormalizeFormat(" Opus "), "opus");

// Processus réels (Linux) : faux yt-dlp qui imite les sorties du vrai outil.
if (OperatingSystem.IsLinux())
{
    var tmp = Path.Combine(Path.GetTempPath(), "mu-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try
    {
        var r = ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "echo out; echo 'ERROR: boom' >&2; exit 3" }, null, null, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Check("processus : code de sortie", r.ExitCode, 3);
        Check("processus : résumé d'erreur", ProcessRunner.Summarize(r.Stderr), "ERROR: boom");
        var slowRun = ProcessRunner.RunAsync("/bin/sleep", new[] { "30" }, null, null, TimeSpan.FromMilliseconds(300), CancellationToken.None).GetAwaiter().GetResult();
        True("processus : délai dépassé", slowRun.TimedOut);
        var quote = ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "printf '%s' \"$1\"", "sh", "a b; echo hacked $(id)" }, null, null, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Check("processus : arguments non interprétés", quote.Stdout.TrimEnd(), "a b; echo hacked $(id)");

        var fakeYt = Path.Combine(tmp, "yt-dlp");
        File.WriteAllText(fakeYt, "#!/bin/sh\nfor a in \"$@\"; do if [ \"$a\" = \"-J\" ]; then echo '" + flat.Replace("\n", string.Empty) + "'; exit 0; fi; done\nwhile [ $# -gt 0 ]; do if [ \"$1\" = \"-P\" ]; then d=\"$2\"; fi; shift; done\nprintf data > \"$d/dQw4w9WgXcQ.m4a\"\n");
        File.SetUnixFileMode(fakeYt, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var fctx = new ToolContext { ToolsDir = tmp, YtDlp = fakeYt, Format = "m4a" };
        var listed = YtDlpClient.ListAsync(fctx, ytLink, CancellationToken.None).GetAwaiter().GetResult();
        Check("faux yt-dlp : liste", listed.Tracks.Count, 2);
        var wd = Path.Combine(tmp, "w1"); Directory.CreateDirectory(wd);
        var got = YtDlpClient.DownloadAsync(fctx, listed.Tracks[0], wd, CancellationToken.None).GetAwaiter().GetResult();
        Check("faux yt-dlp : fichier produit", Path.GetFileName(got), "dQw4w9WgXcQ.m4a");

        var wd2 = Path.Combine(tmp, "w2"); Directory.CreateDirectory(wd2);
        var got2 = YtDlpClient.DownloadAsync(fctx, sl.Tracks[0], wd2, CancellationToken.None).GetAwaiter().GetResult();
        Check("faux yt-dlp : morceau cherché par titre", Path.GetFileName(got2), "dQw4w9WgXcQ.m4a");

        var failing = Path.Combine(tmp, "failing");
        File.WriteAllText(failing, "#!/bin/sh\necho 'ERROR: Video unavailable' >&2\nexit 1\n");
        File.SetUnixFileMode(failing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var wd3 = Path.Combine(tmp, "w3"); Directory.CreateDirectory(wd3);
        string? failMsg = null;
        try { YtDlpClient.DownloadAsync(new ToolContext { YtDlp = failing, Format = "m4a" }, listed.Tracks[0], wd3, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (ImportException ex) { failMsg = ex.Message; }
        Check("échec de téléchargement : message de l'outil", failMsg, "ERROR: Video unavailable");
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
}

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
