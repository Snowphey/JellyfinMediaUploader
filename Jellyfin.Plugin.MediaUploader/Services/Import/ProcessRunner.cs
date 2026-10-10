using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Résultat d'un processus externe.
/// </summary>
/// <param name="ExitCode">Code de sortie (-1 si le processus a été arrêté).</param>
/// <param name="Stdout">Sortie standard.</param>
/// <param name="Stderr">Fin de la sortie d'erreur.</param>
/// <param name="TimedOut">Vrai si le délai a été dépassé.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// Lance les outils externes (yt-dlp) sans passer par un shell : les arguments sont transmis un par un.
/// </summary>
public static class ProcessRunner
{
    private const int MaxStdoutChars = 32 * 1024 * 1024;
    private const int MaxStderrChars = 64 * 1024;

    /// <summary>
    /// Lance un exécutable et attend sa fin.
    /// </summary>
    /// <param name="executable">Chemin de l'exécutable.</param>
    /// <param name="args">Arguments (un par élément, jamais interprétés par un shell).</param>
    /// <param name="workDir">Dossier de travail.</param>
    /// <param name="env">Variables d'environnement à ajouter ou remplacer.</param>
    /// <param name="timeout">Délai maximal.</param>
    /// <param name="ct">Annulation.</param>
    /// <param name="onStdoutLine">Appelé pour chaque ligne de la sortie standard, au fil de l'eau (suivi d'avancement).</param>
    /// <returns>Résultat.</returns>
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct, Action<string>? onStdoutLine = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (workDir is not null)
        {
            info.WorkingDirectory = workDir;
        }

        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                info.Environment[k] = v;
            }
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && stdout.Length < MaxStdoutChars)
            {
                stdout.AppendLine(e.Data);
            }

            if (e.Data is not null)
            {
                onStdoutLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            if (stderr.Length > MaxStderrChars)
            {
                stderr.Remove(0, stderr.Length - MaxStderrChars / 2);
            }
        };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // Laisse les lecteurs asynchrones vider leurs tampons.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    /// <summary>
    /// Dernières lignes utiles d'une sortie d'erreur, pour un message lisible.
    /// </summary>
    /// <param name="stderr">Sortie d'erreur.</param>
    /// <returns>Message court.</returns>
    public static string Summarize(string stderr)
    {
        var lines = System.Text.RegularExpressions.Regex.Replace(stderr, "\u001b\\[[0-9;]*m", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Une erreur de yt-dlp (« ERROR: [youtube] id: … ») dit la vraie cause.
        var ytError = lines.LastOrDefault(l => l.Contains("ERROR: [", StringComparison.Ordinal));
        if (ytError is not null)
        {
            return ytError.Length > 300 ? ytError[..300] + "…" : ytError;
        }

        var errors = lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).ToList();
        var picked = (errors.Count > 0 ? errors : lines).TakeLast(2).ToList();

        // Plantage d'un outil empaqueté (PyInstaller) : le message utile est l'exception qui précède, pas la ligne « Failed to execute script ».
        if (picked.Any(l => l.Contains("PYI-", StringComparison.Ordinal)))
        {
            var cause = lines.LastOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[A-Za-z_][\w.]*(Error|Exception|Exit)\b"));
            if (cause is not null)
            {
                picked = new List<string> { cause };
            }
        }

        var text = string.Join(" · ", picked);
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// Fin d'une sortie, bornée, pour le journal.
    /// </summary>
    /// <param name="text">Sortie.</param>
    /// <returns>Derniers caractères.</returns>
    public static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Déjà terminé.
        }
    }
}
