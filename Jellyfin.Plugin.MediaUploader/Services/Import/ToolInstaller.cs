using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>Système et architecture du serveur.</summary>
public enum ToolPlatform
{
    /// <summary>Linux x86-64.</summary>
    LinuxX64,

    /// <summary>Linux ARM 64 bits.</summary>
    LinuxArm64,

    /// <summary>Windows x86-64.</summary>
    WinX64,

    /// <summary>macOS Apple Silicon.</summary>
    MacArm64,

    /// <summary>macOS Intel.</summary>
    MacX64,

    /// <summary>Non pris en charge.</summary>
    Unknown
}

/// <summary>
/// Installe yt-dlp et deno (exécuteur JavaScript requis par yt-dlp pour YouTube) dans le dossier du plugin,
/// depuis leurs versions officielles sur GitHub. Réservé aux administrateurs.
/// </summary>
public static class ToolInstaller
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    // Les binaires empaquetés (PyInstaller) mettent plusieurs secondes à répondre à « --version » : on garde la réponse tant que le fichier ne change pas.
    private static readonly Dictionary<string, string?> Versions = new();

    /// <summary>
    /// Plateforme courante.
    /// </summary>
    /// <returns>Plateforme.</returns>
    public static ToolPlatform Current()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (OperatingSystem.IsLinux())
        {
            return arm ? ToolPlatform.LinuxArm64 : x64 ? ToolPlatform.LinuxX64 : ToolPlatform.Unknown;
        }

        if (OperatingSystem.IsWindows())
        {
            return x64 ? ToolPlatform.WinX64 : ToolPlatform.Unknown;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? ToolPlatform.MacArm64 : ToolPlatform.MacX64;
        }

        return ToolPlatform.Unknown;
    }

    /// <summary>
    /// Nom de l'archive de yt-dlp à télécharger (version « dossier » : démarre bien plus vite que le fichier unique, qui s'extrait à chaque lancement).
    /// </summary>
    /// <param name="p">Plateforme.</param>
    /// <returns>Nom, ou null.</returns>
    public static string? YtDlpAsset(ToolPlatform p) => p switch
    {
        ToolPlatform.LinuxX64 => "yt-dlp_linux.zip",
        ToolPlatform.LinuxArm64 => "yt-dlp_linux_aarch64.zip",
        ToolPlatform.WinX64 => "yt-dlp_win.zip",
        ToolPlatform.MacArm64 or ToolPlatform.MacX64 => "yt-dlp_macos.zip",
        _ => null
    };

    /// <summary>
    /// Nom de l'archive de deno à télécharger.
    /// </summary>
    /// <param name="p">Plateforme.</param>
    /// <returns>Nom, ou null.</returns>
    public static string? DenoAsset(ToolPlatform p) => p switch
    {
        ToolPlatform.LinuxX64 => "deno-x86_64-unknown-linux-gnu.zip",
        ToolPlatform.LinuxArm64 => "deno-aarch64-unknown-linux-gnu.zip",
        ToolPlatform.WinX64 => "deno-x86_64-pc-windows-msvc.zip",
        ToolPlatform.MacArm64 => "deno-aarch64-apple-darwin.zip",
        ToolPlatform.MacX64 => "deno-x86_64-apple-darwin.zip",
        _ => null
    };

    /// <summary>
    /// Installe ou met à jour un outil dans le dossier du plugin.
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="toolsDir">Dossier des outils.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Chemin de l'outil installé.</returns>
    public static async Task<string> InstallAsync(string tool, string toolsDir, CancellationToken ct)
    {
        // Une seule installation à la fois : deux administrateurs ne doivent pas se disputer le même dossier.
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(tool, toolsDir, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ImportException("Téléchargement impossible : " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImportException("GitHub n'a pas répondu à temps : réessayez dans un instant.");
        }
        catch (InvalidDataException)
        {
            throw new ImportException("Archive téléchargée illisible : réessayez.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportException("Installation impossible (fichiers du dossier des outils inaccessibles ou en cours d'utilisation) : " + ex.Message);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    // Version publiée la plus récente et adresse de téléchargement figée sur cette version : l'archive et son empreinte viennent ainsi de la même publication,
    // et le numéro noté après l'installation est bien celui du fichier installé.
    private static async Task<(string Tag, string BaseUrl)> ResolveReleaseAsync(string repo, string asset, CancellationToken ct)
    {
        using var response = await NoRedirect.GetAsync($"https://github.com/{repo}/releases/latest/download/{asset}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var parts = response.Headers.Location?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 2 || parts[^1] != asset)
        {
            throw new ImportException("Impossible de déterminer la dernière version publiée sur GitHub.");
        }

        var tag = parts[^2];
        return (tag, $"https://github.com/{repo}/releases/download/{tag}/");
    }

    private static async Task<string> InstallCoreAsync(string tool, string toolsDir, CancellationToken ct)
    {
        var platform = Current();
        Directory.CreateDirectory(toolsDir);
        var target = Path.Combine(toolsDir, ToolLocator.FileName(tool));
        var temp = Path.Combine(toolsDir, ".dl-" + Guid.NewGuid().ToString("N"));
        string tag;
        try
        {
            switch (tool)
            {
                case "yt-dlp":
                {
                    var asset = YtDlpAsset(platform) ?? throw new ImportException("Plateforme non prise en charge pour yt-dlp.");
                    string baseUrl;
                    (tag, baseUrl) = await ResolveReleaseAsync("yt-dlp/yt-dlp", asset, ct).ConfigureAwait(false);
                    await DownloadAsync(baseUrl + asset, temp, ct).ConfigureAwait(false);
                    await VerifyAsync(temp, asset, baseUrl + "SHA2-256SUMS", ct).ConfigureAwait(false);
                    var staging = Path.Combine(toolsDir, ".new-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        ZipFile.ExtractToDirectory(temp, staging);
                        var exe = Path.Combine(staging, OperatingSystem.IsWindows() ? "yt-dlp.exe" : Path.GetFileNameWithoutExtension(asset));
                        if (!File.Exists(exe))
                        {
                            throw new ImportException("Archive yt-dlp inattendue.");
                        }

                        var named = Path.Combine(staging, ToolLocator.FileName(tool));
                        if (!string.Equals(exe, named, StringComparison.Ordinal))
                        {
                            File.Move(exe, named);
                            exe = named;
                        }

                        MakeExecutable(exe);
                        var dist = ToolLocator.DistDir(tool, toolsDir);
                        SwapDirectory(staging, dist);

                        // L'ancienne version en fichier unique devient inutile.
                        TryDelete(Path.Combine(toolsDir, ToolLocator.FileName(tool)));
                        target = Path.Combine(dist, Path.GetFileName(exe));
                    }
                    finally
                    {
                        TryDeleteDir(staging);
                    }

                    break;
                }

                case "deno":
                {
                    var asset = DenoAsset(platform) ?? throw new ImportException("Plateforme non prise en charge pour deno.");
                    string baseUrl;
                    (tag, baseUrl) = await ResolveReleaseAsync("denoland/deno", asset, ct).ConfigureAwait(false);
                    var url = baseUrl + asset;
                    await DownloadAsync(url, temp, ct).ConfigureAwait(false);
                    await VerifyAsync(temp, asset, url + ".sha256sum", ct).ConfigureAwait(false);
                    var extracted = temp + ".bin";
                    using (var zip = ZipFile.OpenRead(temp))
                    {
                        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, ToolLocator.FileName("deno"), StringComparison.OrdinalIgnoreCase))
                            ?? throw new ImportException("Archive deno inattendue.");
                        entry.ExtractToFile(extracted, overwrite: true);
                    }

                    File.Delete(temp);
                    Replace(extracted, target);
                    break;
                }

                default:
                    throw new ImportException("Outil inconnu.");
            }
        }
        finally
        {
            TryDelete(temp);
            TryDelete(temp + ".bin");
        }

        lock (Versions)
        {
            Versions.Clear();
        }

        // Le numéro de version est noté à l'installation : l'afficher ne demande plus de lancer l'outil (lent).
        ToolManifest.Record(toolsDir, tool, tag);
        return target;
    }

    // Remplace un dossier par un autre sans jamais rester sans outil : l'ancien est mis de côté, et remis en place si le nouveau ne peut pas entrer.
    private static void SwapDirectory(string staging, string dist)
    {
        string? aside = null;
        try
        {
            if (Directory.Exists(dist))
            {
                aside = dist + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(dist, aside);
            }

            Directory.Move(staging, dist);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (aside is not null && !Directory.Exists(dist) && Directory.Exists(aside))
            {
                Directory.Move(aside, dist);
            }

            throw new ImportException("yt-dlp est en cours d'utilisation (import actif ?) : réessayez dans un instant.");
        }

        if (aside is not null)
        {
            TryDeleteDir(aside);
        }
    }

    /// <summary>
    /// Version déjà connue d'un outil (sans lancer l'outil), ou null.
    /// </summary>
    /// <param name="path">Chemin de l'outil.</param>
    /// <returns>Version, ou null si elle n'a pas encore été lue.</returns>
    public static string? CachedVersion(string path)
    {
        lock (Versions)
        {
            return Versions.TryGetValue(VersionKey(path), out var v) ? v : null;
        }
    }

    private static readonly Dictionary<string, (DateTime At, string? Tag)> Latest = new();

    private static readonly HttpClient NoRedirect = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// Dernière version publiée d'un outil (lue dans la redirection de « releases/latest », sans API ni limite). Gardée une heure.
    /// </summary>
    /// <param name="tool">"yt-dlp" ou "deno".</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Étiquette de version, ou null si GitHub ne répond pas.</returns>
    public static async Task<string?> LatestTagAsync(string tool, CancellationToken ct)
    {
        lock (Latest)
        {
            if (Latest.TryGetValue(tool, out var hit) && DateTime.UtcNow - hit.At < (hit.Tag is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1)))
            {
                return hit.Tag;
            }
        }

        string? tag = null;
        try
        {
            var repo = tool == "deno" ? "denoland/deno" : "yt-dlp/yt-dlp";
            using var response = await NoRedirect.GetAsync($"https://github.com/{repo}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            tag = response.Headers.Location?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            tag = null;
        }

        lock (Latest)
        {
            Latest[tool] = (DateTime.UtcNow, tag);
        }

        return tag;
    }

    /// <summary>
    /// Indique si la version installée est différente de la dernière publiée.
    /// </summary>
    /// <param name="installed">Version installée.</param>
    /// <param name="latest">Dernière version.</param>
    /// <returns>Vrai si une mise à jour existe.</returns>
    public static bool IsOutdated(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        return !string.Equals(installed.Trim().TrimStart('v'), latest.Trim().TrimStart('v'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Version d'un outil. Le résultat est gardé tant que le fichier ne change pas.
    /// </summary>
    /// <param name="path">Chemin de l'outil.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Version, ou null si l'outil ne répond pas.</returns>
    public static async Task<string?> VersionAsync(string path, CancellationToken ct)
    {
        var key = VersionKey(path);
        lock (Versions)
        {
            if (Versions.TryGetValue(key, out var hit) && hit is not null)
            {
                return hit;
            }
        }

        string? version = null;
        try
        {
            var r = await ProcessRunner.RunAsync(path, new[] { "--version" }, null, null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (r.ExitCode == 0)
            {
                version = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (version is { Length: > 60 })
                {
                    version = version[..60];
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            version = null;
        }

        lock (Versions)
        {
            Versions[key] = version;
        }

        return version;
    }

    private static string VersionKey(string path)
    {
        try
        {
            return path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    // Compare l'empreinte SHA-256 du fichier avec celle publiée par le projet (formats « hash  nom » ou « hash »).
    private static async Task VerifyAsync(string file, string assetName, string sumsUrl, CancellationToken ct)
    {
        string text;
        try
        {
            text = await Http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ImportException("Empreinte SHA-256 introuvable : installation refusée par prudence.");
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.EndsWith(assetName, StringComparison.Ordinal) || !l.Contains(' '));
        var expected = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException("Empreinte SHA-256 incorrecte : fichier téléchargé rejeté.");
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void Replace(string from, string to)
    {
        MakeExecutable(from);
        File.Move(from, to, overwrite: true);
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage best effort.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinMediaUploader/1.2");
        return client;
    }
}
