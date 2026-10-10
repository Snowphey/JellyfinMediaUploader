using Jellyfin.Plugin.MediaUploader.Api;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>
/// Range les fichiers reçus à leur destination finale d'après le plan (commun à l'envoi direct et à la confirmation d'un lot).
/// </summary>
public static class UploadCommitter
{
    /// <summary>
    /// Déplace les fichiers temporaires vers leur destination. Les vidéos sont traitées avant leurs sous-titres,
    /// pour que le nom final de la vidéo (si un fichier est apparu entre-temps) serve de base au nom du sous-titre.
    /// </summary>
    /// <param name="work">Éléments du plan et fichier temporaire de chacun (null = pas reçu).</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Un résultat par élément, dans l'ordre d'entrée.</returns>
    public static List<UploadFileResult> Commit(IReadOnlyList<(PlanItem Item, string? Temp)> work, PluginConfiguration config, ILogger logger)
    {
        var results = new Dictionary<string, UploadFileResult>(StringComparer.Ordinal);
        var finalNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (item, temp) in work.OrderBy(w => w.Item.Role switch { "main" => 0, "subtitle" => 1, _ => 2 }))
        {
            results[item.Id] = CommitOne(item, temp, config, logger, finalNames);
        }

        return work.Select(w => results[w.Item.Id]).ToList();
    }

    private static UploadFileResult CommitOne(PlanItem item, string? temp, PluginConfiguration config, ILogger logger, Dictionary<string, string> finalNames)
    {
        try
        {
            if (item.Status is "skipped" or "error" or "duplicate" || item.DestFull is null || item.Root is null)
            {
                // Un doublon garde son fichier reçu : l'utilisateur peut encore choisir de le forcer.
                return new UploadFileResult(item.Name, item.Status == "error" ? "error" : "skipped", null, string.Join(" · ", item.Notes));
            }

            if (temp is null || !File.Exists(temp))
            {
                return new UploadFileResult(item.Name, "error", null, "Fichier non reçu (envoi incomplet)");
            }

            var notes = new List<string>(item.Notes);
            var target = item.DestFull;

            // Un sous-titre renommé suit le nom réel de sa vidéo, même si celui-ci a changé depuis l'aperçu.
            if (item.Role == "subtitle" && item.Pair is { Renamed: true, WithId: { } withId } && finalNames.TryGetValue(withId, out var videoName))
            {
                var wanted = PathBuilder.SanitizeFileName(Planner.SubtitleName(videoName, item.LangSuffix, Path.GetExtension(item.Name)), "file");
                if (!string.Equals(wanted, Path.GetFileName(target), StringComparison.Ordinal))
                {
                    target = Path.Combine(Path.GetDirectoryName(target)!, wanted);
                }
            }

            if (!PathBuilder.IsInside(item.Root, target))
            {
                UploadStore.TryDelete(temp);
                return new UploadFileResult(item.Name, "error", null, "Chemin de destination invalide");
            }

            // Un fichier peut être apparu depuis le calcul du plan.
            if (File.Exists(target) && !config.OverwriteExisting)
            {
                var unique = UniquePath(target);
                if (!string.Equals(Path.GetFileName(unique), Path.GetFileName(item.DestFull), StringComparison.Ordinal))
                {
                    notes.Add($"Nom déjà pris : enregistré sous « {Path.GetFileName(unique)} »");
                }

                target = unique;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            target = MoveWithoutClobber(temp, target, config.OverwriteExisting);
            finalNames[item.Id] = Path.GetFileName(target);

            if (config.ExtractCover && item.Kind == "music" && item.Role == "main" && !string.IsNullOrWhiteSpace(item.Album) && item.Dest!.Contains('/'))
            {
                if (TryExtractCover(target, logger))
                {
                    notes.Add("Pochette extraite des tags");
                }
            }

            logger.LogInformation("MediaUploader: fichier enregistré {Path}", target);
            return new UploadFileResult(
                item.Name,
                "saved",
                target,
                notes.Count > 0 ? string.Join(" · ", notes.Distinct()) : null,
                Path.GetRelativePath(item.Root, target).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            // Le fichier reçu est conservé : une erreur passagère (disque plein, accès refusé) ne doit pas faire perdre un envoi entier, la confirmation peut être refaite.
            logger.LogError(ex, "MediaUploader: échec d'enregistrement de {Name}", item.Name);
            return new UploadFileResult(item.Name, "error", null, ex.Message);
        }
    }

    // Écrit cover.jpg/png/webp dans le dossier de l'album si aucune pochette n'y est déjà (les tags sont relus dans le fichier rangé).
    private static bool TryExtractCover(string finalPath, ILogger logger)
    {
        try
        {
            var tags = AudioTagReader.Read(finalPath, Path.GetExtension(finalPath));
            if (tags?.CoverData is null || tags.CoverExtension is null)
            {
                return false;
            }

            var folder = Path.GetDirectoryName(finalPath)!;
            var hasCover = Directory.EnumerateFiles(folder)
                .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                .Any(n => n is "cover" or "folder" or "poster");
            if (hasCover)
            {
                return false;
            }

            File.WriteAllBytes(Path.Combine(folder, "cover" + tags.CoverExtension), tags.CoverData);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MediaUploader: pochette non extraite pour {Path}", finalPath);
            return false;
        }
    }

    // Deux confirmations simultanées peuvent viser le même nom : si le déplacement échoue parce que la cible vient d'apparaître, on prend le nom libre suivant.
    private static string MoveWithoutClobber(string temp, string target, bool overwrite)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite);
                return target;
            }
            catch (IOException) when (!overwrite && attempt < 5 && File.Exists(target))
            {
                target = UniquePath(target);
            }
        }
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }
}
