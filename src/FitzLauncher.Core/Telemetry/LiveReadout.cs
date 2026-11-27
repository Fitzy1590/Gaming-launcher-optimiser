namespace FitzLauncher.Core.Telemetry;

/// <summary>
/// The live CPU / GPU / RAM row on the game panel (SPEC §18). Pure: when it may read sensors, and how readings
/// are shown.
/// </summary>
public static class LiveReadout
{
    /// <summary>Seconds between readings while the row is live (same as the Hardware page).</summary>
    public const int IntervalSeconds = 2;

    /// <summary>
    /// The row reads sensors only while the launcher is the window in front, showing the Library, with no game
    /// running, not in safe mode and with Low-impact mode off. In every other case it stops and keeps the last
    /// values, so it never takes CPU time from a game.
    /// </summary>
    public static bool ShouldPoll(bool lowImpact, bool gameRunning, bool windowActive, bool libraryVisible, bool safeMode) =>
        !lowImpact && !gameRunning && windowActive && libraryVisible && !safeMode;

    /// <summary>Why the row isn't live, or "" when it is.</summary>
    public static string PausedReason(bool lowImpact, bool gameRunning, bool safeMode) =>
        safeMode ? "Live readings are off in safe mode."
        : lowImpact ? "Live readings are off (Low-impact mode, Settings → Look)."
        : gameRunning ? "Paused while you play, so the game gets the CPU."
        : "";

    public static string Clock(double? mhz) => mhz is > 0 and { } m ? FormattableString.Invariant($"{m / 1000:0.0} GHz") : "–";
    public static string Percent(double? value) => value is { } v ? FormattableString.Invariant($"{Math.Clamp(v, 0, 100):0}%") : "–";
    public static double Meter(double? value) => value is { } v ? Math.Clamp(v, 0, 100) : 0;
    public static string Temp(double? celsius) => celsius is { } c ? FormattableString.Invariant($"{c:0}°C") : "";

    public static string Memory(long totalMb, long availableMb) =>
        totalMb <= 0 ? "–" : FormattableString.Invariant($"{Math.Max(0, totalMb - availableMb) / 1024.0:0.0} / {totalMb / 1024.0:0} GB");

    public static double MemoryPercent(long totalMb, long availableMb) =>
        totalMb <= 0 ? 0 : Math.Clamp((totalMb - availableMb) * 100.0 / totalMb, 0, 100);
}
