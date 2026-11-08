using System.Globalization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>A past session good enough to use as this game's starting point on a screen.</summary>
public sealed record SessionBaseline(SessionSummary Session, int RenderScale, int? Watts, int? LimiterMode)
{
    public string Describe(CultureInfo? ci = null)
    {
        var s = Session;
        var parts = new List<string> { $"{s.DisplayWidth}×{s.DisplayHeight}" };
        if (RenderScale < 100) parts.Add($"{RenderScale}% render scale");
        if (Watts is { } w) parts.Add($"{w} W");
        var held = s.TimeAtTarget is { } t ? $", {t:P0} on target" : "";
        return $"{string.Join(", ", parts)} held {s.AverageFps:0} FPS avg / {s.OnePercentLowFps:0} 1% low{held} ({s.Start.ToString("d MMM", ci ?? CultureInfo.CurrentCulture)})";
    }
}

/// <summary>
/// Picks the best past session for a target on a screen: one that ran close to the cap (avg ≥ 95 %, 1% low ≥ 80 %),
/// preferring the sharpest image (highest render scale), then the lowest power, then the most recent.
/// </summary>
public static class BaselinePicker
{
    public static SessionBaseline? Pick(IEnumerable<SessionSummary> history, int target, string? displayKey)
    {
        var best = history
            .Where(s => s.HasFpsData && s.Duration.TotalMinutes >= 3 && !s.Crashed)
            .Where(s => s.TargetFps == target || s.TargetFps is null)
            .Where(s => displayKey is null || s.DisplayKey is null || s.DisplayKey == displayKey)
            .Where(s => s.AverageFps >= target * 0.95 && s.OnePercentLowFps >= target * 0.8)
            .OrderByDescending(s => s.DisplayKey == displayKey)
            .ThenByDescending(s => s.RenderScale)
            .ThenBy(s => s.PowerWatts ?? int.MaxValue)
            .ThenByDescending(s => s.End)
            .FirstOrDefault();
        return best is null ? null : new SessionBaseline(best, best.RenderScale, best.PowerWatts, best.LimiterMode);
    }
}

/// <summary>What to change in the game's own settings to reach the target on this screen.</summary>
public sealed record GraphicsAdvice(string Headline, IReadOnlyList<string> Steps, int? Width = null, int? Height = null);

/// <summary>
/// Turns the last session's numbers into in-game advice. GPU-bound (high GPU load): lower the resolution or
/// render scale (FPS scales roughly with pixels^0.8), then GPU-heavy settings. CPU-bound: resolution won't help,
/// so it points at CPU-heavy settings instead. Already on target: says so, and where there's room to raise quality.
/// </summary>
public static class GraphicsAdvisor
{
    private static readonly double[] Scales = [1.0, 0.9, 0.83, 0.77, 0.67, 0.59, 0.5];

    public static GraphicsAdvice? Advise(SessionSummary? last, int target, int screenW, int screenH)
    {
        if (last is not { HasFpsData: true } s || target <= 0 || screenW <= 0) return null;
        var gpuBound = s.AvgGpuLoad is null or >= 88;
        var renderW = s.DisplayWidth > 0 ? s.DisplayWidth * s.RenderScale / 100.0 : screenW;
        var renderH = s.DisplayHeight > 0 ? s.DisplayHeight * s.RenderScale / 100.0 : screenH;

        if (s.AverageFps >= target * 0.97 && s.OnePercentLowFps >= target * 0.85)
        {
            var load = s.AvgGpuLoad ?? 100;
            return new GraphicsAdvice($"{target} FPS is holding at these settings.",
                load < 70 ? ["The GPU had spare room (≈" + load.ToString("0", CultureInfo.InvariantCulture) + " % load): you can raise texture quality or anti-aliasing a step and still hold the target."] : []);
        }

        var needed = target * 1.1 / Math.Max(1, s.AverageFps); // 10 % headroom for the lows
        if (!gpuBound)
        {
            return new GraphicsAdvice($"CPU-limited at {s.AverageFps:0} FPS (GPU only {s.AvgGpuLoad:0} % busy): a lower resolution won't help much.",
            [
                "Lower CPU-heavy settings: crowd/NPC density, view or draw distance, shadow distance, physics/simulation quality.",
                "Turn on frame generation (FSR 3 / AMD Fluid Motion Frames) if the game has it.",
                $"Or pick a lower target ({(s.AverageFps >= 60 ? "60" : "45")} FPS) for steadier frame pacing.",
            ]);
        }

        // Pixels needed ≈ current / needed^(1/0.8). Pick the first scale with enough headroom.
        var pixelFactor = Math.Pow(1 / needed, 1 / 0.8);
        var currentScale = Math.Sqrt(renderW * renderH / ((double)screenW * screenH));
        var scale = Scales.FirstOrDefault(x => x * x <= currentScale * currentScale * pixelFactor, 0);
        var steps = new List<string>();
        int? w = null, h = null;
        if (scale > 0)
        {
            w = Even(screenW * scale);
            h = Even(screenH * scale);
            steps.Add($"Set the in-game resolution to {w}×{h} (or render scale {scale * 100:0} % with FSR/XeSS Quality upscaling to {screenW}×{screenH}).");
        }
        else
        {
            steps.Add("Even half resolution won't reach the target: use FSR/XeSS Performance and lower the preset.");
        }
        if (needed > 1.25)
            steps.Add("Lower the GPU-heavy settings a step: shadows, volumetric fog/clouds, ambient occlusion, reflections, ray tracing off.");
        steps.Add("Keep textures where they are: they cost VRAM, not FPS, on this GPU.");
        return new GraphicsAdvice($"GPU-limited: needs about {(needed - 1) * 100:0} % more FPS to hold {target}.", steps, w, h);
    }

    private static int Even(double v) => (int)Math.Round(v / 8) * 8;
}

/// <summary>
/// Live APU power tuning during a session: finds the lowest power limit that holds the FPS target. Misses the
/// target for ~12 s → +3 W; holds it comfortably for ~40 s → −2 W, never back down to a level that already
/// missed. <see cref="Learned"/> is the lowest power that held, saved for next time.
/// </summary>
public sealed class PowerGovernor(int startWatts, int minWatts, int maxWatts, int target)
{
    private readonly Queue<(DateTime at, double fps)> _window = new();
    private DateTime _lastChange = DateTime.MinValue;
    private int? _failedAt;

    public int Current { get; private set; } = Math.Clamp(startWatts, minWatts, maxWatts);
    /// <summary>Lowest power that held the target this session (null until something held).</summary>
    public int? Learned { get; private set; }

    public int? Update(double fps, DateTime now)
    {
        if (fps <= 1 || target <= 0) return null; // paused / loading
        if (_lastChange == DateTime.MinValue) _lastChange = now;
        _window.Enqueue((now, fps));
        while (_window.Count > 0 && now - _window.Peek().at > TimeSpan.FromSeconds(20)) _window.Dequeue();
        var since = now - _lastChange;
        if (_window.Count < 6 || since < TimeSpan.FromSeconds(12)) return null;

        var ordered = _window.Select(x => x.fps).Order().ToList();
        var low = ordered[(int)Math.Floor((ordered.Count - 1) * 0.2)];
        if (low < target * 0.95)
        {
            _failedAt = Math.Max(_failedAt ?? 0, Current);
            if (Learned is { } l && l <= Current) Learned = null; // what "held" earlier didn't hold here
            return Current < maxWatts ? Set(Math.Min(maxWatts, Current + 3), now) : null;
        }
        if (low >= target * 0.985)
        {
            Learned = Learned is { } prev ? Math.Min(prev, Current) : Current;
            var next = Current - 2;
            if (since >= TimeSpan.FromSeconds(40) && next >= minWatts && (_failedAt is null || next > _failedAt)) return Set(next, now);
        }
        return null;
    }

    private int Set(int watts, DateTime now)
    {
        Current = watts;
        _lastChange = now;
        _window.Clear();
        return watts;
    }

    /// <summary>A first guess for a target with no history, scaled from another target's learned power.</summary>
    public static int Estimate(IReadOnlyDictionary<int, int> learned, int target, int fallback)
    {
        if (learned.TryGetValue(target, out var w)) return w;
        if (learned.Count == 0) return fallback;
        var (t, watts) = learned.OrderBy(kv => Math.Abs(kv.Key - target)).Select(kv => (kv.Key, kv.Value)).First();
        return (int)Math.Round(watts * Math.Pow(target / (double)t, 1.3)); // power rises faster than FPS
    }
}
