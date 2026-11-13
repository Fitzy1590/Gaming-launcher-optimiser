namespace FitzLauncher.Core.Telemetry;

/// <summary>Off by default. Watch only decides and logs but changes nothing; it is the first thing to try on a device.</summary>
public enum ClosedLoopMode { Off, WatchOnly, On }

/// <summary>Plain-English answers for the Telemetry page.</summary>
public static class ClosedLoopAdvice
{
    public static string Name(ClosedLoopMode mode) => mode switch
    {
        ClosedLoopMode.WatchOnly => "Watch only",
        ClosedLoopMode.On => "On",
        _ => "Off",
    };

    /// <summary>Why the tuner isn't making decisions right now, or null when it is.</summary>
    /// <param name="fpsError">What the frame capture said when it could not start (for example, it needs administrator rights).</param>
    public static string? WhyNotRunning(ClosedLoopMode mode, bool gameRunning, string? fpsError, bool framesSeen, bool busyAvailable)
    {
        if (mode == ClosedLoopMode.Off) return "It is switched off. Choose Watch only to see what it would do.";
        if (!gameRunning) return "No game is running. It only works while you play.";
        if (!string.IsNullOrWhiteSpace(fpsError)) return fpsError;
        if (!framesSeen) return "Waiting for frames. The first few seconds of a game are ignored.";
        if (!busyAvailable) return "Frame times are coming in, but there are no processor and graphics busy readings yet, so it can't tell which one is the limit. Stutter and heat rules still work.";
        return null;
    }
}
