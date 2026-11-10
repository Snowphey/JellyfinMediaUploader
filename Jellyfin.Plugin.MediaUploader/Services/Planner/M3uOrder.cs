using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaUploader.Services;

/// <summary>Une entrée d'une liste .m3u : chemin (ou adresse) et, si présent, « Artiste - Titre » de la ligne #EXTINF.</summary>
/// <param name="Path">Chemin ou adresse tel qu'écrit.</param>
/// <param name="Info">Texte de #EXTINF après la virgule, ou null.</param>
public sealed record M3uEntry(string Path, string? Info)
{
    /// <summary>Gets un libellé lisible (pour signaler une entrée non reconnue).</summary>
    public string Label => !string.IsNullOrWhiteSpace(Info) ? Info! : M3uOrder.FileName(Path);
}

/// <summary>Un morceau du lot à ordonner.</summary>
/// <param name="Id">Identifiant de l'élément du lot.</param>
/// <param name="ClientPath">Chemin d'origine (nom du fichier envoyé ou importé).</param>
/// <param name="Artist">Artiste (tags).</param>
/// <param name="Title">Titre (tags).</param>
public sealed record M3uCandidate(string Id, string ClientPath, string? Artist, string? Title);

/// <summary>Ordre tiré d'une liste .m3u.</summary>
/// <param name="Order">Identifiants des morceaux du lot, dans l'ordre de la liste.</param>
/// <param name="Entries">Nombre d'entrées de la liste.</param>
/// <param name="Missing">Entrées qui ne correspondent à aucun morceau du lot.</param>
/// <param name="Name">Nom de la liste (#PLAYLIST), ou null.</param>
public sealed record M3uResult(IReadOnlyList<string> Order, int Entries, IReadOnlyList<string> Missing, string? Name);

/// <summary>
/// Lit une liste de lecture .m3u / .m3u8 et retrouve ses morceaux dans un lot : par nom de fichier (avec ou sans extension),
/// sinon par artiste et titre (#EXTINF ou nom du fichier « Artiste - Titre »), comparés avec la même souplesse que les doublons.
/// </summary>
public static class M3uOrder
{
    private static readonly Regex LeadingNumber = new(@"^\s*(\d{1,3}[\s._-]+|[A-Z]?\d{1,2}[.-]\d{1,3}[\s._-]+)", RegexOptions.Compiled);
    private static readonly Regex Dash = new(@"\s+[-–—]\s+", RegexOptions.Compiled);

    /// <summary>
    /// Découpe le texte d'une liste .m3u.
    /// </summary>
    /// <param name="text">Contenu.</param>
    /// <param name="name">Nom donné par #PLAYLIST, ou null.</param>
    /// <returns>Entrées, dans l'ordre.</returns>
    public static List<M3uEntry> Parse(string text, out string? name)
    {
        name = null;
        var entries = new List<M3uEntry>();
        string? info = null;
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',', StringComparison.Ordinal);
                info = comma >= 0 && comma + 1 < line.Length ? line[(comma + 1)..].Trim() : null;
                continue;
            }

            if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase))
            {
                name = line[10..].Trim() is { Length: > 0 } n ? n : null;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            entries.Add(new M3uEntry(line, info));
            info = null;
        }

        return entries;
    }

    /// <summary>
    /// Nom de fichier d'une entrée (chemin Windows ou Unix, adresse file:// ou http, encodée ou non).
    /// </summary>
    /// <param name="path">Chemin.</param>
    /// <returns>Nom du fichier.</returns>
    public static string FileName(string path)
    {
        var p = path.Trim();
        var q = p.IndexOfAny(new[] { '?', '#' });
        if (p.Contains("://", StringComparison.Ordinal) && q > 0)
        {
            p = p[..q];
        }

        if (p.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                p = Uri.UnescapeDataString(p);
            }
            catch (UriFormatException)
            {
                // Laissé tel quel.
            }
        }

        var cut = p.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? p[(cut + 1)..] : p;
    }

    /// <summary>
    /// Ordonne les morceaux du lot d'après la liste.
    /// </summary>
    /// <param name="text">Contenu de la liste.</param>
    /// <param name="items">Morceaux du lot.</param>
    /// <returns>Ordre, et entrées non reconnues.</returns>
    public static M3uResult Match(string text, IReadOnlyList<M3uCandidate> items)
    {
        var entries = Parse(text, out var name);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var missing = new List<string>();

        string Stem(string file) => Path.GetFileNameWithoutExtension(file).Trim();
        var keys = items.ToDictionary(i => i.Id, i => DuplicateMatcher.Key(i.Artist, i.Title), StringComparer.Ordinal);

        foreach (var e in entries)
        {
            var file = FileName(e.Path);
            M3uCandidate? hit = null;

            // 1. Même nom de fichier ; 2. même nom sans l'extension (la liste cite un .flac, le lot contient un .m4a).
            hit = items.FirstOrDefault(i => !used.Contains(i.Id) && string.Equals(FileName(i.ClientPath), file, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(i => !used.Contains(i.Id) && Stem(file).Length > 0 && string.Equals(Stem(FileName(i.ClientPath)), Stem(file), StringComparison.OrdinalIgnoreCase));

            // 3. Artiste et titre : #EXTINF, sinon le nom du fichier sans numéro de piste (« 03 - Artiste - Titre », « Titre »).
            if (hit is null)
            {
                var (artist, title) = Split(e.Info ?? LeadingNumber.Replace(Stem(file), string.Empty));
                foreach (var i in items.Where(i => !used.Contains(i.Id)))
                {
                    var mine = keys[i.Id];
                    if (mine is null || title.Length == 0)
                    {
                        continue;
                    }

                    // Sans artiste dans la liste, celui du morceau est supposé : seul le titre est alors comparé.
                    var theirs = DuplicateMatcher.Key(artist ?? i.Artist, title);
                    if (theirs is not null && DuplicateMatcher.Same(mine, theirs))
                    {
                        hit = i;
                        break;
                    }

                    // « Artiste - Titre » mal découpé (« AC - DC - Thunderstruck ») : le texte entier comme titre.
                    if (artist is not null && DuplicateMatcher.Key(i.Artist, e.Info ?? Stem(file)) is { } whole && DuplicateMatcher.Same(mine, whole))
                    {
                        hit = i;
                        break;
                    }
                }
            }

            if (hit is null)
            {
                missing.Add(e.Label);
                continue;
            }

            used.Add(hit.Id);
            order.Add(hit.Id);
        }

        return new M3uResult(order, entries.Count, missing, name);
    }

    private static (string? Artist, string Title) Split(string text)
    {
        var parts = Dash.Split(text.Trim(), 2);
        return parts.Length == 2 ? (parts[0].Trim(), parts[1].Trim()) : (null, text.Trim());
    }
}
