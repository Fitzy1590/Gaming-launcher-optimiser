using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

// ======================================================================== report card

public sealed record ReportCard(string Grade, int Score, string Smoothness, string Temps, string Stability, string NextStep);

/// <summary>
/// A per-game grade from the last few sessions: smoothness (time on target, 1% lows vs average,
/// stutters), temperatures against your limits, and crashes. Plus the single change most likely to help.
/// </summary>
public static class ReportCards
{
    public static ReportCard? Compute(Game game, IReadOnlyList<SessionSummary> sessions, LauncherSettings s)
    {
        var recent = sessions.Where(x => x.HasFpsData).OrderByDescending(x => x.End).Take(5).ToList();
        if (recent.Count == 0) return null;

        var onTarget = recent.Where(x => x.TimeAtTarget is not null).Select(x => x.TimeAtTarget!.Value).DefaultIfEmpty(0.8).Average();
        var evenness = recent.Average(x => x.AverageFps > 0 ? x.OnePercentLowFps / x.AverageFps : 0);
        var stutterPerHour = recent.Sum(x => x.StutterCount) / Math.Max(recent.Sum(x => x.Duration.TotalHours), 0.1);
        var smooth = Math.Clamp(onTarget * 60 + evenness * 40 - Math.Min(stutterPerHour / 20, 20), 0, 100);

        var maxGpu = recent.Max(x => x.MaxGpuTempC ?? 0);
        var maxCpu = recent.Max(x => x.MaxCpuTempC ?? 0);
        var tempPenalty = Math.Max(0, maxGpu - s.GpuTempAlertC) * 4 + Math.Max(0, maxCpu - s.CpuTempAlertC) * 4;
        var temps = Math.Clamp(100 - tempPenalty, 0, 100);

        var crashes = sessions.OrderByDescending(x => x.End).Take(5).Count(x => x.Crashed);
        var stability = Math.Clamp(100 - crashes * 35, 0, 100);

        var score = (int)Math.Round(smooth * 0.6 + temps * 0.2 + stability * 0.2);
        var grade = score switch { >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 55 => "D", _ => "F" };

        string next;
        if (crashes > 0) next = "Fix stability first: use Roll back on this page to undo the changes made since the last clean session.";
        else if (temps < 70) next = "Running hot: lower the FPS target a step or the power limit; the thermal guard can do this automatically.";
        else if (onTarget < 0.85 && evenness > 0.75) next = "Steady but below target: apply a recommended graphics suggestion, or try a lower target that it can hold.";
        else if (evenness < 0.7 || stutterPerHour > 120) next = "Uneven frame pacing: apply the stutter fixes, and keep the game on the NVMe drive.";
        else if (score >= 90) next = "Running great. Nothing to change.";
        else next = "Let auto-tune keep testing changes between sessions.";

        return new ReportCard(grade, score,
            $"{onTarget:P0} of frames on target · 1% low is {evenness:P0} of average",
            maxGpu > 0 || maxCpu > 0 ? $"Peak GPU {maxGpu:0}°C · CPU {maxCpu:0}°C" : "No temperature data",
            crashes == 0 ? "No crashes in recent sessions" : $"{crashes} crash(es) in the last {Math.Min(5, sessions.Count)} sessions",
            next);
    }
}

// ======================================================================== undervolt

/// <summary>
/// Ryzen Curve Optimizer offset, approached safely: it moves one step (−5) per clean session toward your
/// target, and backs off a step (and stops there) after any crash, freeze or unexpected shutdown.
/// Undervolting can't damage hardware. Too far just causes crashes, which this backs away from.
/// </summary>
public static class UndervoltLadder
{
    public const int Step = 5;

    /// <summary>RyzenAdj's encoding for negative Curve Optimizer counts (two's complement in 20 bits).</summary>
    public static int Encode(int offset) => offset >= 0 ? offset : 0x100000 + offset;

    public static string? AfterCleanSession(LauncherSettings s)
    {
        if (!s.UndervoltEnabled || s.UndervoltSettled || s.UndervoltCurrent <= s.UndervoltTarget) return null;
        s.UndervoltCurrent = Math.Max(s.UndervoltTarget, s.UndervoltCurrent - Step);
        if (s.UndervoltCurrent == s.UndervoltTarget) s.UndervoltSettled = true;
        return $"Undervolt stepped to {s.UndervoltCurrent} for next session (target {s.UndervoltTarget}).";
    }

    public static string? AfterCrash(LauncherSettings s)
    {
        if (!s.UndervoltEnabled || s.UndervoltCurrent == 0) return null;
        s.UndervoltCurrent = Math.Min(0, s.UndervoltCurrent + Step);
        s.UndervoltSettled = true;
        return $"A crash happened with the undervolt active, so it was backed off to {s.UndervoltCurrent} and will stay there.";
    }
}

// ======================================================================== adaptive cap

/// <summary>
/// Radeon Chill-style idle cap: with no keyboard, mouse or controller input for a while (menus, maps,
/// cutscenes, AFK) the cap drops to save heat and power; any input restores it immediately.
/// </summary>
public sealed class IdleCapGovernor(int idleSeconds, int idleCap)
{
    public bool Idle { get; private set; }

    /// <summary>Returns the cap to switch to when the state changes, otherwise null.</summary>
    public int? Update(TimeSpan sinceInput, int normalCap)
    {
        if (idleCap >= normalCap) return null;
        var nowIdle = sinceInput.TotalSeconds >= idleSeconds;
        if (nowIdle == Idle) return null;
        Idle = nowIdle;
        return nowIdle ? idleCap : normalCap;
    }
}

// ======================================================================== background CPU limiter

/// <summary>
/// Process Lasso-style restraint: a background process using more than its share of CPU for two
/// samples in a row gets throttled (lowest priority) until it calms down; never the game or Windows itself.
/// </summary>
public sealed class CpuHogDetector(double thresholdPercent = 12, int strikes = 2)
{
    private readonly Dictionary<int, int> _strikes = [];
    private readonly HashSet<int> _throttled = [];
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "audiodg", "fontdrvhost", "MsMpEng", "RTSS", "MSIAfterburner", "FitzLauncher", "ctfmon", "sihost",
    };

    public IReadOnlyCollection<int> Throttled => _throttled;

    /// <param name="usage">CPU % of the whole machine per process since the last sample.</param>
    /// <returns>Processes to throttle now, and processes to restore now.</returns>
    public (List<int> throttle, List<int> release) Update(IReadOnlyDictionary<int, (string name, double percent)> usage, IReadOnlySet<int> gamePids)
    {
        var throttle = new List<int>();
        var release = new List<int>();
        foreach (var (pid, (name, pct)) in usage)
        {
            if (gamePids.Contains(pid) || Protected.Contains(name)) continue;
            if (pct >= thresholdPercent)
            {
                _strikes[pid] = _strikes.GetValueOrDefault(pid) + 1;
                if (_strikes[pid] >= strikes && _throttled.Add(pid)) throttle.Add(pid);
            }
            else
            {
                _strikes.Remove(pid);
                if (pct < thresholdPercent / 3 && _throttled.Remove(pid)) release.Add(pid);
            }
        }
        foreach (var gone in _throttled.Where(p => !usage.ContainsKey(p)).ToList()) _throttled.Remove(gone);
        return (throttle, release);
    }
}

// ======================================================================== crash rollback

public static class CrashAdvisor
{
    /// <summary>Changes applied since the last clean session: the first suspects after a crash.</summary>
    public static List<AppliedChange> Suspects(Game game) =>
        game.AppliedChanges.Where(c => game.LastGoodSession is null || c.AppliedAt > game.LastGoodSession).ToList();

    public static string Explain(Game game, CrashInfo crash)
    {
        var parts = new List<string> { $"{game.Name} crashed" + (crash.FaultingModule is { } m ? $" in {m}" : "") + "." };
        var module = crash.FaultingModule?.ToLowerInvariant() ?? "";
        if (module is "dxgi.dll" or "d3d11.dll" or "d3d9.dll" or "d3d10core.dll")
            parts.Add("That's a graphics wrapper DLL: DXVK or ReShade are the likely cause. Turn them off for this game.");
        else if (module.StartsWith("nvngx") || module.StartsWith("libxess") || module.StartsWith("amd_fidelityfx"))
            parts.Add("That's an upscaler DLL: revert the DLL update for this game.");
        else if (crash.UndervoltActive)
            parts.Add("The undervolt was active and has been backed off a step.");
        var suspects = Suspects(game);
        if (suspects.Count > 0) parts.Add($"{suspects.Select(c => c.TweakId).Distinct().Count()} change(s) were made since the last clean session; Roll back undoes them.");
        return string.Join(" ", parts);
    }
}
