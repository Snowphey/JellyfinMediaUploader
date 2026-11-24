using System.Text.Json;

namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Un fichier touché par une correction : déplacé et/ou retagué.</summary>
public sealed class UndoFile
{
    /// <summary>Gets or sets le chemin d'origine (relatif à la bibliothèque).</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets le chemin après la correction (égal à <see cref="From"/> s'il n'a pas bougé).</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Gets or sets les tags d'origine (null : tags non modifiés, fichier annexe).</summary>
    public TagSnapshot? Before { get; set; }
}

/// <summary>Un élément mis à la corbeille par une correction.</summary>
public sealed class UndoTrash
{
    /// <summary>Gets or sets l'identifiant dans la corbeille.</summary>
    public string TrashId { get; set; } = string.Empty;

    /// <summary>Gets or sets le chemin d'origine (relatif).</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Une correction de l'onglet « Santé » (ou de l'éditeur de tags), avec de quoi la défaire.</summary>
public sealed class UndoEntry
{
    /// <summary>Gets or sets l'identifiant.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets la date (UTC).</summary>
    public DateTime At { get; set; }

    /// <summary>Gets or sets l'utilisateur.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets la description.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets la bibliothèque ("music"…).</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets les fichiers déplacés ou retagués.</summary>
    public List<UndoFile> Files { get; set; } = new();

    /// <summary>Gets or sets les éléments mis à la corbeille.</summary>
    public List<UndoTrash> Trashed { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether la correction a été annulée.</summary>
    public bool Undone { get; set; }
}

/// <summary>
/// Journal des dernières corrections (30 au plus), gardé sur disque : chaque correction peut être annulée
/// (fichiers remis à leur place avec leurs tags d'origine, éléments ressortis de la corbeille).
/// </summary>
public static class UndoJournal
{
    private const int Keep = 30;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string? Dir => Plugin.Instance is { } p ? System.IO.Path.Combine(p.DataFolderPath, "undo") : null;

    /// <summary>
    /// Enregistre une correction (rien si elle n'a rien touché).
    /// </summary>
    /// <param name="entry">Correction.</param>
    /// <returns>Identifiant, ou null.</returns>
    public static string? Add(UndoEntry entry)
    {
        if (entry.Files.Count == 0 && entry.Trashed.Count == 0 || Dir is not { } dir)
        {
            return null;
        }

        entry.Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        entry.At = DateTime.UtcNow;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(System.IO.Path.Combine(dir, entry.Id + ".json"), JsonSerializer.Serialize(entry, Json));
                foreach (var old in Directory.EnumerateFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
                {
                    File.Delete(old);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return entry.Id;
    }

    /// <summary>
    /// Dernières corrections, les plus récentes d'abord.
    /// </summary>
    /// <returns>Corrections.</returns>
    public static List<UndoEntry> List()
    {
        if (Dir is not { } dir || !Directory.Exists(dir))
        {
            return new List<UndoEntry>();
        }

        lock (Gate)
        {
            return Directory.EnumerateFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Select(Read).Where(e => e is not null).Cast<UndoEntry>().ToList();
        }
    }

    /// <summary>
    /// Une correction.
    /// </summary>
    /// <param name="id">Identifiant.</param>
    /// <returns>Correction, ou null.</returns>
    public static UndoEntry? Get(string id)
    {
        if (Dir is not { } dir || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{14}-[0-9a-f]{6}$"))
        {
            return null;
        }

        lock (Gate)
        {
            return Read(System.IO.Path.Combine(dir, id + ".json"));
        }
    }

    /// <summary>
    /// Marque une correction comme annulée.
    /// </summary>
    /// <param name="entry">Correction.</param>
    public static void MarkUndone(UndoEntry entry)
    {
        if (Dir is not { } dir)
        {
            return;
        }

        entry.Undone = true;
        lock (Gate)
        {
            try
            {
                File.WriteAllText(System.IO.Path.Combine(dir, entry.Id + ".json"), JsonSerializer.Serialize(entry, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sans conséquence : l'annulation a eu lieu.
            }
        }
    }

    private static UndoEntry? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UndoEntry>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
