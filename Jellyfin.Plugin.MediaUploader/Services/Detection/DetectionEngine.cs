using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaUploader.Configuration;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Tags lus dans un fichier audio (version minimale, sans dépendance à TagLib).
/// </summary>
/// <param name="Artist">Artiste d'album, à défaut interprète.</param>
/// <param name="Album">Album.</param>
/// <param name="Title">Titre du morceau.</param>
public sealed record TagInfo(string? Artist, string? Album, string? Title);

/// <summary>
/// Résultat de la détection pour un fichier.
/// </summary>
public sealed class Detection
{
    /// <summary>Gets or sets le type : "movie", "series" ou "music" (null si indéterminé).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets le titre du film ou de la série.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets l'année.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la saison.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets l'épisode.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets l'artiste.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets l'album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets le niveau de confiance : "sure", "guess" ou "unknown".</summary>
    public string Confidence { get; set; } = "unknown";

    /// <summary>Gets or sets l'identifiant de la règle qui a reconnu le fichier.</summary>
    public string? RuleId { get; set; }

    /// <summary>Gets or sets le nom de la règle qui a reconnu le fichier.</summary>
    public string? RuleName { get; set; }

    /// <summary>Gets les remarques à montrer à l'utilisateur.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Gets a value indicating whether un titre (film/série) ou un artiste/album a été trouvé.</summary>
    public bool HasIdentity => Kind == "music" ? !string.IsNullOrWhiteSpace(Artist) || !string.IsNullOrWhiteSpace(Album) : !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Une règle compilée (ou son erreur de compilation).
/// </summary>
/// <param name="Rule">Règle d'origine.</param>
/// <param name="Regex">Expression compilée (null si invalide).</param>
/// <param name="Error">Erreur de validation, sinon null.</param>
public sealed record CompiledRule(DetectionRule Rule, Regex? Regex, string? Error);

/// <summary>
/// Moteur de détection : applique les règles configurées (regex à groupes nommés) à un nom ou un chemin de fichier.
/// Ne dépend de rien d'autre que du .NET : il est testé seul.
/// </summary>
public sealed class DetectionEngine
{
    /// <summary>Durée maximale d'une expression, pour qu'une regex pathologique ne bloque pas le serveur.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex DoubleEpisode = new(@"^[ ._]?-?[ ._]?E\d{1,3}(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<CompiledRule> _rules;
    private readonly bool _folderContext;

    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="DetectionEngine"/>.
    /// </summary>
    /// <param name="rules">Règles, dans l'ordre de priorité.</param>
    /// <param name="useFolderContext">Autorise le repli sur les noms de dossiers (titre d'une série, film dans un dossier « Titre (2019) »).</param>
    public DetectionEngine(IEnumerable<DetectionRule> rules, bool useFolderContext = true)
    {
        _rules = rules.Select(Compile).ToList();
        _folderContext = useFolderContext;
    }

    /// <summary>Gets les règles, compilées ou en erreur.</summary>
    public IReadOnlyList<CompiledRule> Rules => _rules;

    /// <summary>
    /// Valide une règle sans l'activer : type, cible, regex compilable et groupes nécessaires.
    /// </summary>
    /// <param name="rule">Règle.</param>
    /// <returns>Message d'erreur, ou null si la règle est valide.</returns>
    public static string? Validate(DetectionRule rule)
    {
        return Compile(rule).Error;
    }

    /// <summary>
    /// Détecte film ou série pour une vidéo ou un sous-titre.
    /// </summary>
    /// <param name="clientPath">Chemin relatif côté client (le simple nom si le fichier n'est pas dans un dossier déposé).</param>
    /// <param name="forcedKind">"movie" ou "series" si l'utilisateur impose le type, sinon null.</param>
    /// <returns>Résultat (jamais null).</returns>
    public Detection DetectVideo(string clientPath, string? forcedKind)
    {
        var path = NameTools.NormalizePath(clientPath);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        var stem = NameTools.StemOf(fileName);
        var parents = segments.Length > 1 ? segments[..^1] : Array.Empty<string>();
        var pathNoExt = string.Join('/', parents.Append(stem));
        var notes = new List<string>();

        foreach (var cr in _rules)
        {
            var rule = cr.Rule;
            if (cr.Regex is null || !rule.Enabled || rule.Kind == "music")
            {
                continue;
            }

            if (forcedKind is not null && rule.Kind != forcedKind)
            {
                continue;
            }

            if (rule.ExplicitOnly && forcedKind != rule.Kind)
            {
                continue;
            }

            var det = TryRule(cr, rule.Target == "path" ? pathNoExt : stem, notes);
            if (det is null)
            {
                continue;
            }

            if (det.Kind == "series" && string.IsNullOrWhiteSpace(det.Title))
            {
                var (t, y) = FolderTitle(parents);
                if (t is not null)
                {
                    det.Title = t;
                    det.Year ??= y;
                    det.Confidence = "guess";
                    det.Notes.Add("Titre de la série lu dans le dossier");
                }
            }

            det.Notes.InsertRange(0, notes);
            return det;
        }

        // Repli : un film rangé dans un dossier « Titre (2019) » dont le fichier n'a pas de nom exploitable.
        if (_folderContext && forcedKind != "series" && parents.Length > 0)
        {
            foreach (var cr in _rules)
            {
                var rule = cr.Rule;
                if (cr.Regex is null || !rule.Enabled || rule.Kind != "movie" || rule.Target != "name" || rule.ExplicitOnly)
                {
                    continue;
                }

                var det = TryRule(cr, parents[^1], notes);
                if (det is not null)
                {
                    det.Confidence = "guess";
                    det.Notes.Add("Titre et année lus dans le dossier parent");
                    det.Notes.InsertRange(0, notes);
                    return det;
                }
            }
        }

        var unknown = new Detection { Kind = forcedKind ?? "movie", Confidence = "unknown" };
        unknown.Notes.AddRange(notes);
        unknown.Notes.Add(forcedKind == "series" ? "Aucune règle ne reconnaît saison et épisode" : "Aucune règle ne reconnaît ce nom");
        return unknown;
    }

    /// <summary>
    /// Détecte artiste et album d'un morceau : les tags d'abord, les règles « musique » pour combler les manques.
    /// </summary>
    /// <param name="clientPath">Chemin relatif côté client.</param>
    /// <param name="tags">Tags lus (null si illisibles ou pas encore reçus).</param>
    /// <returns>Résultat (jamais null).</returns>
    public Detection DetectMusic(string clientPath, TagInfo? tags)
    {
        var det = new Detection { Kind = "music", Artist = CleanText(tags?.Artist), Album = CleanText(tags?.Album) };
        if (det.Artist is not null && det.Album is not null)
        {
            det.Confidence = "sure";
            return det;
        }

        var path = NameTools.NormalizePath(clientPath);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var pathNoExt = string.Join('/', segments.Length > 1 ? segments[..^1].Append(stem) : new[] { stem });

        foreach (var cr in _rules)
        {
            var rule = cr.Rule;
            if (cr.Regex is null || !rule.Enabled || rule.Kind != "music")
            {
                continue;
            }

            Match m;
            try
            {
                m = cr.Regex.Match(rule.Target == "path" ? pathNoExt : stem);
            }
            catch (RegexMatchTimeoutException)
            {
                det.Notes.Add($"Règle « {rule.Name} » trop lente : ignorée");
                continue;
            }

            if (!m.Success)
            {
                continue;
            }

            var filled = false;
            if (det.Artist is null && CleanText(Group(m, "artist")) is { } a)
            {
                det.Artist = a;
                filled = true;
            }

            if (det.Album is null && CleanText(Group(m, "album")) is { } b)
            {
                det.Album = b;
                filled = true;
            }

            if (filled)
            {
                det.RuleId = rule.Id;
                det.RuleName = rule.Name;
                det.Confidence = "guess";
                det.Notes.Add(tags is null ? "Artiste/album déduits du chemin ou du nom (tags absents)" : "Tags incomplets : artiste/album complétés d'après le chemin ou le nom");
                return det;
            }
        }

        if (det.Artist is not null || det.Album is not null)
        {
            det.Confidence = "sure";
        }
        else
        {
            det.Notes.Add(tags is null ? "Tags illisibles ou absents" : "Pas d'artiste ni d'album dans les tags");
        }

        return det;
    }

    private static CompiledRule Compile(DetectionRule rule)
    {
        string? Fail(string message) => message;

        var kind = rule.Kind ?? string.Empty;
        if (kind is not ("movie" or "series" or "music"))
        {
            return new CompiledRule(rule, null, Fail("Type inconnu (movie, series ou music)."));
        }

        if (rule.Target is not ("name" or "path"))
        {
            return new CompiledRule(rule, null, Fail("Cible inconnue (name ou path)."));
        }

        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return new CompiledRule(rule, null, Fail("Expression vide."));
        }

        Regex regex;
        try
        {
            regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            return new CompiledRule(rule, null, Fail("Expression invalide : " + ex.Message));
        }

        var groups = regex.GetGroupNames();
        bool Has(string g) => groups.Contains(g);
        var error = kind switch
        {
            "movie" when !Has("title") => "Un film nécessite le groupe nommé (?<title>…).",
            "series" when !Has("episode") => "Une série nécessite le groupe nommé (?<episode>…).",
            "music" when !Has("artist") && !Has("album") => "La musique nécessite (?<artist>…) ou (?<album>…).",
            _ => null
        };

        return new CompiledRule(rule, error is null ? regex : null, error);
    }

    private static string? Group(Match m, string name) => m.Groups[name].Success ? m.Groups[name].Value : null;

    private static string? CleanText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length == 0 ? null : t;
    }

    private static int? ParseInt(string? s)
    {
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int MaxYear => DateTime.UtcNow.Year + 2;

    // Titre de série d'après les dossiers parents (en sautant « Season 2 », « Specials »...).
    private static (string? Title, int? Year) FolderTitle(string[] parents)
    {
        for (var i = parents.Length - 1; i >= 0; i--)
        {
            if (NameTools.IsSeasonFolder(parents[i]))
            {
                continue;
            }

            var t = NameTools.NormalizeTitle(parents[i]);
            if (t.Length == 0)
            {
                continue;
            }

            var (title, year) = NameTools.SplitYear(t, MaxYear);
            return (title, year);
        }

        return (null, null);
    }

    private Detection? TryRule(CompiledRule cr, string input, List<string> notes)
    {
        var rule = cr.Rule;
        Match m;
        try
        {
            m = cr.Regex!.Match(input);

            // Une année invraisemblable (« Film.2019.2099 ») : on réessaie sur ce qui précède.
            for (var guard = 0; guard < 5 && m.Success && m.Groups["year"].Success; guard++)
            {
                var y = ParseInt(m.Groups["year"].Value);
                if (y is null || (y <= MaxYear && y >= 1880) || m.Groups["year"].Index <= 0)
                {
                    break;
                }

                m = cr.Regex.Match(input[..m.Groups["year"].Index]);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            notes.Add($"Règle « {rule.Name} » trop lente : ignorée");
            return null;
        }

        if (!m.Success)
        {
            return null;
        }

        var det = new Detection { Kind = rule.Kind, RuleId = rule.Id, RuleName = rule.Name, Confidence = rule.Guess ? "guess" : "sure" };
        var title = NameTools.NormalizeTitle(Group(m, "title"));
        var year = ParseInt(Group(m, "year"));
        if (year is not null && (year > MaxYear || year < 1880))
        {
            return null;
        }

        if (rule.Kind == "movie")
        {
            if (title.Length == 0)
            {
                return null;
            }

            det.Title = title;
            det.Year = year;
            return det;
        }

        // Série.
        var episode = ParseInt(Group(m, "episode"));
        if (episode is null)
        {
            return null;
        }

        var season = ParseInt(Group(m, "season"));
        if (season is null)
        {
            season = Math.Max(0, rule.DefaultSeason);
            det.Confidence = "guess";
            det.Notes.Add($"Saison {season} supposée (aucune saison dans le nom)");
        }

        if (title.Length > 0)
        {
            var (t, y) = NameTools.SplitYear(title, MaxYear);
            det.Title = t;
            det.Year = year ?? y;
        }
        else
        {
            det.Year = year;
        }

        det.Season = season;
        det.Episode = episode;

        var tail = input[(m.Index + m.Length)..];
        if (DoubleEpisode.IsMatch(tail))
        {
            det.Notes.Add($"Double épisode : seul l'épisode {episode} est pris en compte");
            det.Confidence = "guess";
        }

        return det;
    }
}
