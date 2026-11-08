using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

public enum CoolingMode { Off, Balanced, Cool, Coolest }

/// <summary>What a cooling mode changes for a session.</summary>
public sealed record CoolingPlan(
    CoolingMode Mode,
    /// <summary>Windows "processor performance boost mode" while playing (null = leave alone): 0 off, 3 efficient.</summary>
    int? BoostMode,
    /// <summary>Multiplier on the APU power limit.</summary>
    double PowerScale,
    /// <summary>How many targets lower the FPS cap goes (e.g. 110 → 100).</summary>
    int CapSteps,
    /// <summary>Extra °C of margin so the thermal guard acts earlier.</summary>
    int GuardExtraMarginC,
    /// <summary>The thermal guard also trims power (APUs) when it steps the cap down.</summary>
    bool GuardLowersPower,
    string Description)
{
    public static CoolingPlan For(CoolingMode mode) => mode switch
    {
        CoolingMode.Balanced => new(mode, 3, 1.0, 0, 2, false,
            "CPU boost set to efficient: the CPU still boosts, but less eagerly. Usually 3–8°C cooler with almost no FPS change."),
        CoolingMode.Cool => new(mode, 0, 0.9, 0, 4, true,
            "CPU boost off and power limit −10%: much cooler and quieter. GPU-bound games barely change; CPU-heavy games can lose a few FPS. The thermal guard steps in earlier and also trims power."),
        CoolingMode.Coolest => new(mode, 0, 0.8, 1, 6, true,
            "CPU boost off, power −20% and the FPS cap one step lower (e.g. 110 → 100): the coolest and quietest setting, for long sessions, warm rooms or playing on your lap."),
        _ => new(CoolingMode.Off, null, 1.0, 0, 0, false, "No cooling changes: full performance."),
    };

    /// <summary>The cap <paramref name="steps"/> targets lower on the 60/90/100/110/117 ladder.</summary>
    public static int StepDown(int cap, int steps)
    {
        int[] ladder = [.. new[] { 60 }.Concat(LauncherSettings.FpsTargets)];
        var idx = Array.FindLastIndex(ladder, t => t <= cap);
        if (idx < 0) return cap;
        return ladder[Math.Max(0, idx - steps)];
    }
}

/// <summary>An FPS cap remembered for one game on one PC and screen.</summary>
public sealed class DeviceCap
{
    /// <summary>0 = no cap.</summary>
    public int Cap { get; set; }
    public string Label { get; set; } = "";
    public DateTimeOffset Saved { get; set; } = DateTimeOffset.Now;
}

public static class DeviceCaps
{
    /// <summary>"PCNAME|1920x1200@120": a PC and screen combination (handheld screen vs monitor, desktop vs handheld).</summary>
    public static string Key(string machine, DisplayInfo display) => $"{machine.ToUpperInvariant()}|{display.Key}";
    public static string Label(string machine, DisplayInfo display) => $"{machine} · {display.Label}";

    /// <summary>The saved cap for this PC and screen: null when none is saved (use the game's target), 0 for "no cap".</summary>
    public static int? For(Game game, string machine, DisplayInfo? display) =>
        game.CapPerDevice && display is not null && game.DeviceCaps.TryGetValue(Key(machine, display), out var c) ? c.Cap : null;

    public static void Save(Game game, string machine, DisplayInfo display, int cap) =>
        game.DeviceCaps[Key(machine, display)] = new DeviceCap { Cap = cap, Label = Label(machine, display) };
}
