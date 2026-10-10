using System.Text.Json;

namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>
/// Où en sont les quotas de téléchargement, et ce qu'il faudrait pour en lancer un certain nombre.
/// </summary>
/// <param name="MaxPerHour">Plafond horaire (0 : sans limite).</param>
/// <param name="UsedHour">Téléchargements démarrés pendant la dernière heure.</param>
/// <param name="MaxPerDay">Plafond quotidien (0 : sans limite).</param>
/// <param name="UsedDay">Téléchargements démarrés pendant les dernières 24 h.</param>
/// <param name="MinDelay">Délai minimal entre deux téléchargements (s).</param>
/// <param name="MaxDelay">Délai maximal entre deux téléchargements (s).</param>
/// <param name="PausedFor">Durée restante d'une suspension après blocage, ou null.</param>
/// <param name="NextSlotIn">Attente avant qu'une place se libère si un plafond est atteint, ou null.</param>
/// <param name="PaceIn">Attente imposée par le délai aléatoire entre deux téléchargements, ou null.</param>
/// <param name="Tracks">Nombre de morceaux de la simulation.</param>
/// <param name="LastStartIn">Dans combien de temps le dernier de ces morceaux démarrerait.</param>
/// <param name="Delayed">Combien de ces morceaux sont retardés par un plafond (et pas seulement par le délai habituel).</param>
public sealed record QuotaSnapshot(int MaxPerHour, int UsedHour, int MaxPerDay, int UsedDay, int MinDelay, int MaxDelay, TimeSpan? PausedFor, TimeSpan? NextSlotIn, TimeSpan? PaceIn, int Tracks, TimeSpan LastStartIn, int Delayed);

/// <summary>
/// Cadence des téléchargements pour ne pas ressembler à un robot : l'adresse du serveur est la même pour tous les utilisateurs,
/// donc le rythme (délai aléatoire entre deux téléchargements, plafonds par heure et par jour) est commun à tout le plugin.
/// Quand YouTube signale une limitation ou une vérification anti-robot, tout est suspendu, de plus en plus longtemps si cela se répète.
/// </summary>
public static class ImportThrottle
{
    /// <summary>Durée de la première suspension après un blocage.</summary>
    public static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan MaxPause = TimeSpan.FromHours(6);
    private static readonly object Gate = new();
    private static readonly Queue<DateTime> Starts = new();
    private static DateTime _nextStart = DateTime.MinValue;
    private static DateTime _pausedUntil = DateTime.MinValue;
    private static int _blocks;
    private static string? _file;

    /// <summary>Gets une horloge remplaçable (tests).</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Gets le tirage aléatoire entre deux bornes en secondes, remplaçable (tests).</summary>
    public static Func<int, int, double> Pick { get; set; } = (min, max) => min + (Random.Shared.NextDouble() * (max - min));

    /// <summary>
    /// Indique où garder l'historique (fichier quota.json du dossier des outils) et le relit s'il existe : les compteurs survivent ainsi à un redémarrage de Jellyfin.
    /// </summary>
    /// <param name="toolsDir">Dossier des outils du plugin (vide : rien n'est écrit).</param>
    public static void Use(string? toolsDir)
    {
        lock (Gate)
        {
            EnsureLoaded(toolsDir);
        }
    }

    /// <summary>
    /// Fin de la suspension en cours, ou null si les téléchargements ne sont pas suspendus.
    /// </summary>
    /// <returns>Date (UTC) ou null.</returns>
    public static DateTime? PausedUntil()
    {
        lock (Gate)
        {
            return _pausedUntil > Clock() ? _pausedUntil : null;
        }
    }

    /// <summary>
    /// État des quotas et simulation du lancement de <paramref name="tracks"/> morceaux (délai moyen entre deux, plafonds, suspension éventuelle). Ne réserve rien.
    /// </summary>
    /// <param name="c">Réglages.</param>
    /// <param name="tracks">Nombre de morceaux à simuler (0 : état seul).</param>
    /// <returns>Instantané.</returns>
    public static QuotaSnapshot Snapshot(ToolContext c, int tracks)
    {
        lock (Gate)
        {
            EnsureLoaded(c.ToolsDir);
            var now = Clock();
            var starts = Starts.Where(s => now - s < TimeSpan.FromDays(1)).OrderBy(s => s).ToList();
            var usedHour = starts.Count(s => now - s < TimeSpan.FromHours(1));
            var min = Math.Max(2, c.MinDelaySeconds);
            var max = Math.Max(min, c.MaxDelaySeconds);
            var gap = TimeSpan.FromSeconds((min + max) / 2.0);

            TimeSpan? paused = _pausedUntil > now ? _pausedUntil - now : null;
            TimeSpan? next = null;
            if (c.MaxPerHour > 0 && usedHour >= c.MaxPerHour)
            {
                next = starts.Where(s => now - s < TimeSpan.FromHours(1)).OrderBy(s => s).ElementAt(usedHour - c.MaxPerHour) + TimeSpan.FromHours(1) - now;
            }

            if (c.MaxPerDay > 0 && starts.Count >= c.MaxPerDay)
            {
                var free = starts[starts.Count - c.MaxPerDay] + TimeSpan.FromDays(1) - now;
                if (next is null || free > next)
                {
                    next = free;
                }
            }

            var t = now;
            if (_pausedUntil > t)
            {
                t = _pausedUntil;
            }

            if (_nextStart > t)
            {
                t = _nextStart;
            }

            // « Retardé » : démarre plus tard que sans aucun plafond (au rythme habituel du délai aléatoire).
            var baseline = t;
            var delayed = 0;
            var last = TimeSpan.Zero;
            for (var i = 0; i < Math.Min(tracks, 5000); i++)
            {
                t = FirstAllowed(starts, t, c);
                if (t > baseline + (gap * i) + TimeSpan.FromSeconds(1))
                {
                    delayed++;
                }

                starts.Add(t);
                last = t - now;
                t += gap;
            }

            TimeSpan? pace = _nextStart > now ? _nextStart - now : null;
            return new QuotaSnapshot(c.MaxPerHour, usedHour, c.MaxPerDay, starts.Count(s => s <= now && now - s < TimeSpan.FromDays(1)), min, max, paused, next, pace, tracks, last, delayed);
        }
    }

    // Premier instant, à partir de t, où un démarrage respecte les plafonds horaire et quotidien (starts : démarrages connus, simulés compris).
    private static DateTime FirstAllowed(List<DateTime> starts, DateTime t, ToolContext c)
    {
        for (var guard = 0; guard < 10000; guard++)
        {
            var moved = false;
            if (c.MaxPerHour > 0)
            {
                var inHour = starts.Where(s => s <= t && t - s < TimeSpan.FromHours(1)).OrderBy(s => s).ToList();
                if (inHour.Count >= c.MaxPerHour)
                {
                    t = inHour[inHour.Count - c.MaxPerHour] + TimeSpan.FromHours(1);
                    moved = true;
                }
            }

            if (c.MaxPerDay > 0)
            {
                var inDay = starts.Where(s => s <= t && t - s < TimeSpan.FromDays(1)).OrderBy(s => s).ToList();
                if (inDay.Count >= c.MaxPerDay)
                {
                    t = inDay[inDay.Count - c.MaxPerDay] + TimeSpan.FromDays(1);
                    moved = true;
                }
            }

            if (!moved)
            {
                return t;
            }
        }

        return t;
    }

    /// <summary>
    /// Attend son tour : délai depuis le dernier démarrage, plafonds horaire et quotidien, suspension éventuelle. Réserve ensuite le créneau.
    /// </summary>
    /// <param name="c">Réglages.</param>
    /// <param name="ct">Annulation.</param>
    /// <returns>Tâche terminée quand le téléchargement peut démarrer.</returns>
    public static async Task WaitTurnAsync(ToolContext c, CancellationToken ct)
    {
        while (true)
        {
            var wait = TryReserve(c);
            if (wait == TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Signale un téléchargement réussi : la suspension suivante repart de la durée de base.
    /// </summary>
    public static void ReportSuccess()
    {
        lock (Gate)
        {
            if (_blocks != 0)
            {
                _blocks = 0;
                Save();
            }
        }
    }

    /// <summary>
    /// Signale une limitation ou une vérification anti-robot : tous les téléchargements sont suspendus (30 min, puis le double à chaque récidive, 6 h au plus).
    /// </summary>
    /// <returns>Durée de la suspension.</returns>
    public static TimeSpan ReportBlocked()
    {
        lock (Gate)
        {
            // Plusieurs téléchargements en cours touchés par le même incident ne comptent que pour un seul blocage.
            var now = Clock();
            if (_pausedUntil > now)
            {
                return _pausedUntil - now;
            }

            var pause = TimeSpan.FromTicks(Math.Min(MaxPause.Ticks, FirstPause.Ticks << Math.Min(_blocks, 4)));
            _blocks++;
            _pausedUntil = now + pause;
            Save();
            return pause;
        }
    }

    /// <summary>
    /// Indique si un message d'erreur d'un outil vient d'une limitation ou d'une détection de robot (et non d'une vidéo indisponible).
    /// </summary>
    /// <param name="text">Message ou sortie d'erreur.</param>
    /// <returns>Vrai si YouTube limite ou suspecte le serveur.</returns>
    public static bool IsBlock(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limited", StringComparison.OrdinalIgnoreCase)
            || text.Contains("try again later", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("Sign in to confirm you", StringComparison.OrdinalIgnoreCase) && !text.Contains("age", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class PersistedState
    {
        public List<DateTime>? Starts { get; set; }

        public DateTime NextStart { get; set; }

        public DateTime PausedUntil { get; set; }

        public int Blocks { get; set; }
    }

    // À appeler sous Gate.
    private static void EnsureLoaded(string? toolsDir)
    {
        if (string.IsNullOrWhiteSpace(toolsDir))
        {
            return;
        }

        var path = Path.Combine(toolsDir, "quota.json");
        if (string.Equals(_file, path, StringComparison.Ordinal))
        {
            return;
        }

        _file = path;
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path)) is not { } state)
            {
                return;
            }

            var now = Clock();
            var known = Starts.ToHashSet();
            foreach (var s in (state.Starts ?? new List<DateTime>()).Where(s => now - s < TimeSpan.FromDays(1) && s <= now + TimeSpan.FromMinutes(1)).OrderBy(s => s))
            {
                if (known.Add(s))
                {
                    Starts.Enqueue(s);
                }
            }

            if (state.NextStart > _nextStart && state.NextStart < now + TimeSpan.FromHours(1))
            {
                _nextStart = state.NextStart;
            }

            if (state.PausedUntil > _pausedUntil && state.PausedUntil < now + MaxPause + TimeSpan.FromMinutes(1))
            {
                _pausedUntil = state.PausedUntil;
            }

            _blocks = Math.Max(_blocks, Math.Clamp(state.Blocks, 0, 10));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Historique illisible : on repart de zéro plutôt que de bloquer les imports.
        }
    }

    // À appeler sous Gate.
    private static void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new PersistedState { Starts = Starts.ToList(), NextStart = _nextStart, PausedUntil = _pausedUntil, Blocks = _blocks }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans fichier, les compteurs ne survivent pas à un redémarrage : sans gravité pour l'import en cours.
        }
    }

    /// <summary>
    /// Remet l'état à zéro (tests).
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Starts.Clear();
            _nextStart = DateTime.MinValue;
            _pausedUntil = DateTime.MinValue;
            _blocks = 0;
            _file = null;
        }
    }

    /// <summary>
    /// Réserve un créneau de téléchargement s'il est libre.
    /// </summary>
    /// <param name="c">Réglages.</param>
    /// <returns>Zéro si le créneau est réservé (on peut démarrer), sinon le temps à attendre avant de réessayer.</returns>
    public static TimeSpan TryReserve(ToolContext c)
    {
        lock (Gate)
        {
            return ReserveLocked(c);
        }
    }

    private static TimeSpan ReserveLocked(ToolContext c)
    {
        EnsureLoaded(c.ToolsDir);
        var now = Clock();
        while (Starts.Count > 0 && now - Starts.Peek() > TimeSpan.FromDays(1))
        {
            Starts.Dequeue();
        }

        var until = _pausedUntil;
        if (_nextStart > until)
        {
            until = _nextStart;
        }

        if (c.MaxPerHour > 0)
        {
            var inHour = Starts.Where(s => now - s < TimeSpan.FromHours(1)).OrderBy(s => s).ToList();
            if (inHour.Count >= c.MaxPerHour)
            {
                var free = inHour[inHour.Count - c.MaxPerHour] + TimeSpan.FromHours(1);
                if (free > until)
                {
                    until = free;
                }
            }
        }

        if (c.MaxPerDay > 0 && Starts.Count >= c.MaxPerDay)
        {
            var free = Starts.OrderBy(s => s).ElementAt(Starts.Count - c.MaxPerDay) + TimeSpan.FromDays(1);
            if (free > until)
            {
                until = free;
            }
        }

        if (until > now)
        {
            return until - now;
        }

        var min = Math.Max(2, c.MinDelaySeconds);
        var max = Math.Max(min, c.MaxDelaySeconds);
        Starts.Enqueue(now);
        _nextStart = now + TimeSpan.FromSeconds(Pick(min, max));
        Save();
        return TimeSpan.Zero;
    }
}
