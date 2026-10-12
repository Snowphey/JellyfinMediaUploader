namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Textes des lignes ajoutées au journal d'activité de Jellyfin : une ligne par envoi, avec les dossiers touchés en résumé et la liste des fichiers en détail.
/// </summary>
public static class ActivityText
{
    private const int MaxDetailLines = 40;
    private const int MaxFolders = 3;

    /// <summary>
    /// Taille lisible (« 12,3 Mo »).
    /// </summary>
    /// <param name="bytes">Octets.</param>
    /// <returns>Texte.</returns>
    public static string Size(long bytes)
    {
        string[] units = { "o", "Ko", "Mo", "Go", "To" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + " " + units[unit];
    }

    /// <summary>
    /// Compte lisible (« 3 dossiers et 12 fichiers »).
    /// </summary>
    /// <param name="folders">Nombre de dossiers (ceux supprimés ou déplacés eux-mêmes).</param>
    /// <param name="files">Nombre de fichiers.</param>
    /// <returns>Texte.</returns>
    public static string Count(int folders, int files)
    {
        var parts = new List<string>();
        if (folders > 0)
        {
            parts.Add(folders + (folders > 1 ? " dossiers" : " dossier"));
        }

        if (files > 0 || folders == 0)
        {
            parts.Add(files + (files > 1 ? " fichiers" : " fichier"));
        }

        return string.Join(" et ", parts);
    }

    /// <summary>
    /// Détail d'une action de bibliothèque : une ligne par élément (« chemin : précision »), bornée.
    /// </summary>
    /// <param name="lines">Lignes.</param>
    /// <param name="max">Nombre maximal de lignes gardées.</param>
    /// <returns>Texte.</returns>
    public static string LibraryDetail(IReadOnlyList<string> lines, int max = 80)
    {
        var shown = lines.Take(max).ToList();
        if (lines.Count > shown.Count)
        {
            shown.Add($"… et {lines.Count - shown.Count} autre(s)");
        }

        return string.Join("\n", shown);
    }

    /// <summary>
    /// Résume des fichiers rangés dans la bibliothèque.
    /// </summary>
    /// <param name="files">Type (« music », « movie », « series ») et chemin de destination relatif au dossier de la bibliothèque, pour chaque fichier.</param>
    /// <returns>Libellé court (« 12 fichiers »), résumé des dossiers, et détail fichier par fichier.</returns>
    public static (string What, string Summary, string Detail) Summarize(IReadOnlyList<(string? Kind, string Destination)> files)
    {
        var what = files.Count == 1 ? "1 fichier" : files.Count + " fichiers";

        // Dossier à afficher : « Artiste / Album » pour la musique, « Titre (Année) » pour un film ou une série.
        string Folder((string? Kind, string Destination) f)
        {
            var parts = f.Destination.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folders = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
            if (folders.Length == 0)
            {
                return "racine de la bibliothèque";
            }

            return f.Kind == "music" ? string.Join(" / ", folders.Take(2)) : folders[0];
        }

        var distinct = files.Select(Folder).Distinct(StringComparer.Ordinal).ToList();
        var kinds = files.Select(f => f.Kind switch { "music" => "musique", "movie" => "film", "series" => "série", _ => null }).Where(k => k is not null).Distinct().ToList();
        var summary = string.Join(" ; ", distinct.Take(MaxFolders)) + (distinct.Count > MaxFolders ? $" … (+{distinct.Count - MaxFolders})" : string.Empty);
        if (kinds.Count > 0)
        {
            summary = string.Join("/", kinds) + " : " + summary;
        }

        var lines = files.Take(MaxDetailLines).Select(f => f.Destination.Replace('\\', '/')).ToList();
        if (files.Count > MaxDetailLines)
        {
            lines.Add($"… et {files.Count - MaxDetailLines} autre(s)");
        }

        return (what, summary, string.Join("\n", lines));
    }
}
