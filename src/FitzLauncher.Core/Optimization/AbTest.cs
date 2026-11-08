using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

public enum AbKind { Cooling, FpsCap, Manual }

/// <summary>One side of an A/B test.</summary>
public sealed class AbSetup
{
    public string Label { get; set; } = "";
    public CoolingMode? Cooling { get; set; }
    /// <summary>FPS cap for this run (0 = no cap).</summary>
    public int? Cap { get; set; }
}

public sealed class AbRun
{
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public int StutterCount { get; set; }
    public double? MaxGpuTempC { get; set; }
    public double? MaxCpuTempC { get; set; }
    public DateTimeOffset Taken { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// A/B benchmark: play the same part twice, once per setup. Each run records a fixed-length benchmark
/// automatically after a warm-up; then the two are compared on smoothness, FPS and heat.
/// </summary>
public sealed class AbTest
{
    public AbKind Kind { get; set; }
    public AbSetup A { get; set; } = new();
    public AbSetup B { get; set; } = new();
    public int WarmupSeconds { get; set; } = 60;
    public int Seconds { get; set; } = 90;
    public AbRun? ResultA { get; set; }
    public AbRun? ResultB { get; set; }
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;

    public bool Done => ResultA is not null && ResultB is not null;
    /// <summary>The setup the next Play uses.</summary>
    public AbSetup? Next => ResultA is null ? A : ResultB is null ? B : null;
    public string NextName => ResultA is null ? "A" : "B";

    public string Verdict()
    {
        if (ResultA is not { } a || ResultB is not { } b) return "";
        static double Pct(double x, double y) => y <= 0 ? 0 : (x - y) / y * 100;
        var lows = Pct(b.OnePercentLowFps, a.OnePercentLowFps);
        var avg = Pct(b.AverageFps, a.AverageFps);
        var hotA = Math.Max(a.MaxGpuTempC ?? 0, a.MaxCpuTempC ?? 0);
        var hotB = Math.Max(b.MaxGpuTempC ?? 0, b.MaxCpuTempC ?? 0);
        var cooler = hotA > 0 && hotB > 0 ? hotA - hotB : 0;

        string Compare(string better, string worse, double l, double av, double c, int stA, int stB) =>
            $"{better} vs {worse}: 1% lows {Sign(l)}%, average FPS {Sign(av)}%" + (c != 0 ? $", {Math.Abs(c):0}°C {(c > 0 ? "cooler" : "hotter")}" : "") + $", stutters {stB} vs {stA}.";

        var detail = Compare($"B ({B.Label})", $"A ({A.Label})", lows, avg, cooler, a.StutterCount, b.StutterCount);
        // Smoothness first (1% lows), then heat, then average FPS; within ±3% counts as a tie.
        var score = lows + Math.Clamp(cooler, -10, 10) * 0.6 + avg * 0.3 - (b.StutterCount - a.StutterCount) * 0.5;
        var winner = Math.Abs(score) < 3 ? null : score > 0 ? $"B ({B.Label})" : $"A ({A.Label})";
        return winner is null
            ? $"Too close to call: pick whichever runs cooler or quieter. {detail}"
            : $"{winner} is better. {detail}";

        static string Sign(double x) => x >= 0 ? $"+{x:0.#}" : $"{x:0.#}";
    }
}
