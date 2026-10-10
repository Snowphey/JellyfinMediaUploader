namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Outils externes et réglages d'import, figés au lancement d'un import.
/// </summary>
public sealed class ToolContext
{
    /// <summary>Gets or sets le dossier où le plugin installe ses outils (yt-dlp, deno).</summary>
    public string ToolsDir { get; set; } = string.Empty;

    /// <summary>Gets or sets le chemin de yt-dlp (null = introuvable).</summary>
    public string? YtDlp { get; set; }

    /// <summary>Gets or sets le chemin de deno, exécuteur JavaScript dont yt-dlp a besoin pour YouTube (null = introuvable).</summary>
    public string? Deno { get; set; }

    /// <summary>Gets or sets le chemin de ffmpeg (celui de Jellyfin en général).</summary>
    public string? Ffmpeg { get; set; }

    /// <summary>Gets or sets le fichier de cookies (facultatif).</summary>
    public string? CookiesPath { get; set; }

    /// <summary>Gets or sets le format audio : "m4a", "opus" ou "mp3".</summary>
    public string Format { get; set; } = "m4a";

    /// <summary>Gets or sets le nombre de téléchargements simultanés.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>Gets or sets le délai minimal entre deux téléchargements, en secondes.</summary>
    public int MinDelaySeconds { get; set; } = 8;

    /// <summary>Gets or sets le délai maximal entre deux téléchargements, en secondes.</summary>
    public int MaxDelaySeconds { get; set; } = 25;

    /// <summary>Gets or sets le nombre maximal de téléchargements par heure (0 : sans limite).</summary>
    public int MaxPerHour { get; set; } = 100;

    /// <summary>Gets or sets le nombre maximal de téléchargements par jour (0 : sans limite).</summary>
    public int MaxPerDay { get; set; } = 400;

    /// <summary>Gets or sets le dossier de la bibliothèque musique.</summary>
    public string MusicRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether les outils journalisent en détail (nouvelle tentative après un échec, pour en connaître la cause).</summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Copie de ces réglages avec la journalisation détaillée.
    /// </summary>
    /// <returns>Copie.</returns>
    public ToolContext WithVerbose() => new()
    {
        ToolsDir = ToolsDir, YtDlp = YtDlp, Deno = Deno, Ffmpeg = Ffmpeg, CookiesPath = CookiesPath,
        Format = Format, Concurrency = Concurrency, MusicRoot = MusicRoot, Verbose = true,
        MinDelaySeconds = MinDelaySeconds, MaxDelaySeconds = MaxDelaySeconds, MaxPerHour = MaxPerHour, MaxPerDay = MaxPerDay
    };

    /// <summary>Gets the extension du format avec le point.</summary>
    public string Extension => "." + Format;

    /// <summary>Gets l'environnement des processus : outils du plugin dans le PATH (deno), dossier personnel inscriptible.</summary>
    public Dictionary<string, string> ChildEnvironment()
    {
        var sep = Path.PathSeparator.ToString();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var home = Path.Combine(ToolsDir, "home");
        try
        {
            Directory.CreateDirectory(home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            home = Path.GetTempPath();
        }

        return new Dictionary<string, string>
        {
            ["PATH"] = ToolsDir + sep + path,
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"),

            // Sorties lisibles : pas de couleurs, pas de retour à la ligne à 80 colonnes (le journal les coupait en plein milieu des messages).
            ["COLUMNS"] = "400",
            ["NO_COLOR"] = "1",
            ["TERM"] = "dumb"
        };
    }

    /// <summary>
    /// Normalise le format demandé.
    /// </summary>
    /// <param name="value">Valeur de la configuration.</param>
    /// <returns>"m4a", "opus" ou "mp3".</returns>
    public static string NormalizeFormat(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "opus" => "opus",
            "mp3" => "mp3",
            _ => "m4a"
        };
    }
}

/// <summary>
/// Recherche des outils externes.
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// Nom du fichier exécutable d'un outil sur le système courant.
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <returns>Nom de fichier.</returns>
    public static string FileName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>
    /// Dossier où le plugin installe un outil livré en plusieurs fichiers (yt-dlp).
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="toolsDir">Dossier des outils du plugin.</param>
    /// <returns>Chemin du dossier.</returns>
    public static string DistDir(string tool, string toolsDir) => Path.Combine(toolsDir, tool + "-dist");

    /// <summary>
    /// Exemplaire installé par le plugin (version « dossier », sinon fichier seul d'une installation plus ancienne), ou null.
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="toolsDir">Dossier des outils du plugin.</param>
    /// <returns>Chemin complet, ou null.</returns>
    public static string? FindInstalled(string tool, string toolsDir)
    {
        var inDist = Path.Combine(DistDir(tool, toolsDir), FileName(tool));
        if (File.Exists(inDist))
        {
            return inDist;
        }

        var local = Path.Combine(toolsDir, FileName(tool));
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// Cherche un outil : chemin réglé par l'administrateur, puis dossier des outils du plugin, puis PATH.
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="configured">Chemin réglé dans les paramètres (facultatif).</param>
    /// <param name="toolsDir">Dossier des outils du plugin.</param>
    /// <returns>Chemin complet, ou null.</returns>
    public static string? Find(string tool, string? configured, string toolsDir)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim();
            if (File.Exists(path))
            {
                return path;
            }

            if (Directory.Exists(path) && File.Exists(Path.Combine(path, FileName(tool))))
            {
                return Path.Combine(path, FileName(tool));
            }
        }

        var installed = FindInstalled(tool, toolsDir);
        if (installed is not null)
        {
            return installed;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), FileName(tool));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Entrée de PATH invalide.
            }
        }

        return null;
    }
}

/// <summary>
/// Mémoire des versions installées par le plugin (fichier tools.json du dossier des outils) : évite de lancer un outil, lent à démarrer, rien que pour lire son numéro.
/// </summary>
public static class ToolManifest
{
    private static readonly object Gate = new();

    /// <summary>
    /// Note la version qui vient d'être installée.
    /// </summary>
    /// <param name="toolsDir">Dossier des outils.</param>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="tag">Version (null : inconnue, l'entrée est effacée).</param>
    public static void Record(string toolsDir, string tool, string? tag)
    {
        lock (Gate)
        {
            var map = Load(toolsDir);
            if (tag is null)
            {
                map.Remove(tool);
            }
            else
            {
                map[tool] = tag;
            }

            try
            {
                File.WriteAllText(Path.Combine(toolsDir, "tools.json"), System.Text.Json.JsonSerializer.Serialize(map));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sans ce fichier, la version sera lue en lançant l'outil.
            }
        }
    }

    /// <summary>
    /// Version notée pour un outil si <paramref name="path"/> est bien l'exemplaire installé par le plugin.
    /// </summary>
    /// <param name="toolsDir">Dossier des outils.</param>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="path">Chemin de l'outil utilisé.</param>
    /// <returns>Version, ou null.</returns>
    public static string? Known(string toolsDir, string tool, string path)
    {
        var ours = ToolLocator.FindInstalled(tool, toolsDir);
        if (ours is null || !string.Equals(Path.GetFullPath(ours), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Gate)
        {
            return Load(toolsDir).TryGetValue(tool, out var tag) ? tag : null;
        }
    }

    private static Dictionary<string, string> Load(string toolsDir)
    {
        try
        {
            var file = Path.Combine(toolsDir, "tools.json");
            if (File.Exists(file))
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // Fichier absent ou abîmé : on repart de zéro.
        }

        return new();
    }
}
