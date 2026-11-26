namespace FitzLauncher.Core.Telemetry;

/// <summary>Plain-English frame-pacing summary: the 1% low as a share of the average FPS.</summary>
public static class FramePacing
{
    /// <summary>1.0 is perfectly even. Null when there is no reading.</summary>
    public static double? Consistency(double avgFps, double lowFps) =>
        avgFps > 0 && lowFps > 0 ? Math.Min(1.0, lowFps / avgFps) : null;

    public static string Describe(double avgFps, double lowFps) => Consistency(avgFps, lowFps) switch
    {
        null => "–",
        var c when c >= 0.80 => $"{c * 100:0}% · smooth",
        var c when c >= 0.60 => $"{c * 100:0}% · some hitches",
        var c => $"{c * 100:0}% · uneven",
    };
}
