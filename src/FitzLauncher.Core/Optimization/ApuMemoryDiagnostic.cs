using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>Memory figures read from Windows and DXGI just before launch.</summary>
public sealed class GraphicsMemorySnapshot
{
    /// <summary>RAM Windows can see (already excludes the part reserved for graphics).</summary>
    public ulong TotalPhysBytes { get; init; }
    public ulong AvailPhysBytes { get; init; }
    /// <summary>Graphics memory reserved from RAM (the BIOS "UMA frame buffer").</summary>
    public ulong DedicatedVideoBytes { get; init; }
    /// <summary>The most system RAM Windows lets the GPU borrow on top. A ceiling, not an allocation.</summary>
    public ulong SharedSystemBytes { get; init; }
    public bool DxgiOk { get; init; }
}

public enum MemoryLevel { Unknown, Ok, Tight, Low }

public sealed record ApuMemoryReport(
    MemoryLevel Level, string Headline, string Detail, string Advice,
    double TotalGb, double ReservedGb, double HeadroomGb, int? SuggestedReservedGb)
{
    public static ApuMemoryReport Unknown { get; } = new(MemoryLevel.Unknown, "", "", "", 0, 0, 0, null);
    public bool ShowBadge => Level is MemoryLevel.Tight or MemoryLevel.Low;
}

/// <summary>
/// Pre-launch check for APUs and handhelds, where part of the RAM is reserved for graphics: does that leave the game
/// enough memory? Advice only, nothing is changed. The thresholds are rules of thumb, not measurements.
/// </summary>
public static class ApuMemoryDiagnostic
{
    private const double Gib = 1024.0 * 1024 * 1024;
    /// <summary>Windows, the launcher and overlays.</summary>
    public const double OsReserveGb = 3.0;
    /// <summary>What a game is assumed to need in RAM when we don't know (typical at handheld settings).</summary>
    public const double DefaultGameNeedGb = 6.0;
    private static readonly int[] Steps = [2, 4, 6, 8, 12, 16];

    public static ApuMemoryReport Evaluate(HardwareProfile? hw, GraphicsMemorySnapshot? m, double? gameNeedGb = null)
    {
        if (hw is not { IsApu: true } || m is not { DxgiOk: true } || m.TotalPhysBytes == 0) return ApuMemoryReport.Unknown;

        var osVisible = m.TotalPhysBytes / Gib;
        var reserved = m.DedicatedVideoBytes / Gib;
        var physical = Math.Round(osVisible + reserved);
        var need = gameNeedGb is > 0 ? gameNeedGb.Value : DefaultGameNeedGb;

        // What the game can really count on: all RAM minus the OS, or what is free right now if that's less.
        var usable = Math.Min(osVisible - OsReserveGb, m.AvailPhysBytes / Gib);
        var headroom = usable - need;
        var level = headroom >= 2 ? MemoryLevel.Ok : headroom >= 0 ? MemoryLevel.Tight : MemoryLevel.Low;
        if (level == MemoryLevel.Ok)
            return new ApuMemoryReport(level, "", "", "", physical, reserved, headroom, null);

        int? suggested = null;
        foreach (var step in Steps.Reverse())
        {
            if (step >= Math.Round(reserved)) continue;
            if (physical - step - OsReserveGb - need >= 2) { suggested = step; break; }
        }
        if (suggested is null && Math.Round(reserved) > 4) suggested = 4;

        var headline = level == MemoryLevel.Low
            ? $"Memory is very tight: {reserved:0.#} GB of your {physical:0} GB is reserved for graphics"
            : $"Memory is a bit tight: {reserved:0.#} GB of your {physical:0} GB is reserved for graphics";
        var detail = $"Windows can use {osVisible:0.#} GB and {m.AvailPhysBytes / Gib:0.#} GB is free now. " +
                     $"After Windows, a game that needs about {need:0.#} GB leaves {(headroom >= 0 ? headroom.ToString("0.#") + " GB spare" : (-headroom).ToString("0.#") + " GB short")}. " +
                     (level == MemoryLevel.Low ? "The game may stutter or hitch when memory runs out." : "Most games will be fine, but big ones may hitch.");
        var advice = suggested is { } s
            ? $"Try {s} GB for graphics memory. {WhereToChange(hw)} Fitz never changes this for you. Closing other apps before you play also helps."
            : "Closing other apps before you play frees the most memory; changing the graphics memory setting won't help much on this device.";
        return new ApuMemoryReport(level, headline, detail, advice, physical, reserved, headroom, suggested);
    }

    private static string WhereToChange(HardwareProfile hw)
    {
        var maker = hw.SystemManufacturer ?? "";
        if (maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            return "In Armoury Crate: Settings, Operating Mode, GPU Settings, Memory Assigned to GPU (then restart).";
        if (maker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase))
            return "In the BIOS: UMA Frame Buffer Size (on some models Legion Space has it too), then restart.";
        return "In the BIOS (UMA Frame Buffer Size) or your device's control app, then restart.";
    }
}
