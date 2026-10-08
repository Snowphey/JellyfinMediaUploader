namespace Jellyfin.Plugin.MediaUploader.Configuration;

/// <summary>
/// Règles de détection fournies par défaut. Utilisées tant que l'administrateur n'a pas personnalisé la liste.
/// L'ordre compte : les séries (formes sans ambiguïté) avant les films, le chemin avant l'année.
/// </summary>
public static class DefaultRules
{
    /// <summary>
    /// Crée une nouvelle liste des règles par défaut.
    /// </summary>
    /// <returns>Règles.</returns>
    public static List<DetectionRule> Create()
    {
        return new List<DetectionRule>
        {
            new()
            {
                Id = "series-sxxexx",
                Name = "Série : S01E02",
                Kind = "series",
                Pattern = @"^(?<title>.*?)[ ._\-\[(]*(?<![A-Za-z0-9])S(?<season>\d{1,2})[ ._\-]?E(?<episode>\d{1,3})(?!\d)",
                Description = "Show.Name.S01E02.1080p, Show Name (2019) S01E02 - Titre"
            },
            new()
            {
                Id = "series-1x02",
                Name = "Série : 1x02",
                Kind = "series",
                Pattern = @"^(?<title>.*?)[ ._\-\[(]*(?<![A-Za-z0-9])(?<season>\d{1,2})x(?<episode>\d{1,3})(?![0-9A-Za-z])",
                Description = "Show Name - 1x02 (ne prend pas « 1920x1080 » pour un épisode)"
            },
            new()
            {
                Id = "series-words",
                Name = "Série : Season 1 Episode 2",
                Kind = "series",
                Pattern = @"^(?<title>.*?)[ ._\-\[(]*(?<![A-Za-z0-9])(?:Season|Saison|Staffel|Temporada)[ ._\-]*(?<season>\d{1,2})[ ._\-]*(?:Episode|Épisode|Ep|E)[ ._\-]*(?<episode>\d{1,3})(?!\d)",
                Description = "Show Name Season 1 Episode 2, Titre Saison 2 Episode 5"
            },
            new()
            {
                Id = "series-anime-season",
                Name = "Série/animé : Titre S2 - 05",
                Kind = "series",
                Pattern = @"^(?:\[[^\]]*\][ ._]*)?(?<title>.+?)[ ._]+S(?<season>\d{1,2})[ ._]*-[ ._]*(?<episode>\d{1,4})(?:v\d)?(?=[ ._\[(]|$)",
                Description = "[Groupe] Titre S2 - 05 [1080p]"
            },
            new()
            {
                Id = "series-folders",
                Name = "Série : dossier Série/Season N/fichier",
                Kind = "series",
                Target = "path",
                Guess = true,
                Pattern = @"^(?:.*/)?(?<title>[^/]+?)/(?:Season|Saison|S)[ ._\-]*(?<season>\d{1,2})/(?:[^/]*?[ ._\-\[(])?(?<episode>\d{1,3})(?=[ ._\-\])]|$)[^/]*$",
                Description = "Utilise les dossiers d'un dossier déposé : Série (2019)/Season 2/05 - Titre.mkv"
            },
            new()
            {
                Id = "movie-year",
                Name = "Film : Titre (année)",
                Kind = "movie",
                Pattern = @"^(?<title>.+)[ ._\-(\[]+(?<year>(?:19|20)\d{2})(?=$|[ ._\-)\]])",
                Description = "Titre (2019), Titre.2019.1080p.BluRay-GRP, Titre [2019]. Prend la dernière année plausible."
            },
            new()
            {
                Id = "series-anime-absolute",
                Name = "Animé sans saison : [Groupe] Titre - 05",
                Kind = "series",
                ExplicitOnly = true,
                Guess = true,
                DefaultSeason = 1,
                Pattern = @"^(?:\[[^\]]*\][ ._]*)?(?<title>.+?)[ ._]*-[ ._]*(?<episode>\d{1,4})(?:v\d)?(?=[ ._\[(]|$)",
                Description = "Seulement si vous choisissez « Série / animé » (sinon un film pourrait être pris pour un épisode). Saison 1 supposée."
            },
            new()
            {
                Id = "movie-noyear",
                Name = "Film sans année (coupe aux étiquettes de qualité)",
                Kind = "movie",
                Enabled = false,
                Guess = true,
                Pattern = @"^(?<title>.+?)(?:[ ._\-]+(?:2160p|1080p|720p|480p|BluRay|BDRip|WEB-?DL|WEBRip|HDTV|DVDRip|x264|x265|HEVC|MULTi|VOSTFR|FRENCH|TRUEFRENCH)\b.*)?$",
                Description = "Désactivée par défaut : accepte un film sans année, toujours « à vérifier »."
            },
            new()
            {
                Id = "music-folders",
                Name = "Musique : dossiers Artiste/Album/piste",
                Kind = "music",
                Target = "path",
                Guess = true,
                Pattern = @"^(?:.*/)?(?<artist>[^/]+)/(?<album>[^/]+)/[^/]+$",
                Description = "Complète les tags manquants avec les deux dossiers parents d'un dossier déposé."
            },
            new()
            {
                Id = "music-artist-title",
                Name = "Musique : Artiste - Titre",
                Kind = "music",
                Enabled = false,
                Guess = true,
                Pattern = @"^(?<artist>.+?)\s-\s(?<title>.+)$",
                Description = "Désactivée par défaut : complète l'artiste manquant d'après le nom du fichier."
            }
        };
    }
}
