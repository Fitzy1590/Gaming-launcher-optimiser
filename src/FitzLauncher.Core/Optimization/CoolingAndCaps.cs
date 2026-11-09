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
    /// <summary>How many °C below the thermal guard's trigger this mode starts it.</summary>
    int GuardExtraMarginC,
    /// <summary>The thermal guard also trims power (APUs) when it steps the cap down.</summary>
    bool GuardLowersPower,
    string Description)
{
    /// <summary>
    /// Every mode is reactive: nothing changes until the chip gets hot. Cooler modes just start the thermal guard
    /// at a lower temperature (and let it trim APU power too); Coolest also makes the CPU boost less eagerly.
    /// </summary>
    public static CoolingPlan For(CoolingMode mode) => mode switch
    {
        CoolingMode.Balanced => new(mode, null, 1.0, 0, 3, false,
            "Full performance until the chip reaches about 87°C, then the FPS cap eases down a step at a time until temperatures level out, and comes back as they drop."),
        CoolingMode.Cool => new(mode, null, 1.0, 0, 5, true,
            "Full performance until about 85°C; then the cap eases down a step at a time and the APU power limit is trimmed too, both coming back as it cools."),
        CoolingMode.Coolest => new(mode, 3, 1.0, 0, 10, true,
            "CPU boost set to efficient (quieter, usually no FPS change), and the guard starts at about 80°C, trimming the cap and power until things level out. For long sessions or warm rooms."),
        _ => new(CoolingMode.Off, null, 1.0, 0, 0, false, "Full performance. The thermal guard still eases the cap a step if the chip reaches 90°C (Settings → Thermal guard)."),
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
