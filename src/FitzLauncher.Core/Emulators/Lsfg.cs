using System.Xml.Linq;

namespace FitzLauncher.Core.Emulators;

/// <summary>What the 2x frame generation profile asks for. 60 real frames become 120 shown frames.</summary>
public sealed record LsfgPlan(int BaseFps, int Multiplier, int OutputFps, int GpuCeilingPercent)
{
    public string Summary => $"{BaseFps} FPS → {OutputFps} FPS (Lossless Scaling {Multiplier}×, GPU kept under {GpuCeilingPercent}%)";
}

public static class Lsfg
{
    public const int DefaultGpuCeiling = 95;

    /// <summary>2x profile. The base cap is what RTSS holds the game to; the output is what the screen gets. Out-of-range input is pulled back to something safe.</summary>
    public static LsfgPlan Plan(int baseFps = 60, int multiplier = 2, int gpuCeiling = DefaultGpuCeiling)
    {
        var m = Math.Clamp(multiplier, 2, 2); // 2x only: higher multipliers queue more frames and add input lag
        var b = Math.Clamp(baseFps, 30, 90);
        return new LsfgPlan(b, m, b * m, Math.Clamp(gpuCeiling, 70, 99));
    }

    /// <summary>
    /// Steps the base frame cap down when the GPU stays over the ceiling (frame generation needs GPU headroom or the
    /// real frames stutter), and back up once there is clear room. Pure: the caller feeds one GPU load sample per tick.
    /// </summary>
    public sealed class GpuGuard(LsfgPlan plan, int overTicks = 3, int underTicks = 10, int floorFps = 40, int step = 5)
    {
        private int _over, _under;
        public int CurrentCap { get; private set; } = plan.BaseFps;

        /// <summary>The new cap when it should change, otherwise null.</summary>
        public int? Update(double? gpuLoadPercent)
        {
            if (gpuLoadPercent is not { } load) return null;
            if (load > plan.GpuCeilingPercent) { _over++; _under = 0; }
            else if (load < plan.GpuCeilingPercent - 12) { _under++; _over = 0; }
            else { _over = 0; _under = 0; }

            if (_over >= overTicks && CurrentCap > floorFps)
            {
                _over = 0;
                CurrentCap = Math.Max(floorFps, CurrentCap - step);
                return CurrentCap;
            }
            if (_under >= underTicks && CurrentCap < plan.BaseFps)
            {
                _under = 0;
                CurrentCap = Math.Min(plan.BaseFps, CurrentCap + step);
                return CurrentCap;
            }
            return null;
        }
    }

    // Settings.xml element names are from memory and unverified until a device test (SPEC §15). Only elements that already
    // exist are changed, so an unknown version gets a preview and no edit.
    public static readonly string[] AllowedKeys = ["LSFG3Multiplier", "LSFG3Mode", "FrameGeneration"];

    public sealed record SettingChange(string Key, string? Old, string New);

    /// <summary>Key-level patch of Lossless Scaling's Settings.xml. Returns the new text and what changed; unchanged text when nothing matched.</summary>
    public static (string Xml, List<SettingChange> Changes) Patch(string xml, LsfgPlan plan)
    {
        var changes = new List<SettingChange>();
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return (xml, changes); }

        var wanted = new Dictionary<string, string>
        {
            ["LSFG3Multiplier"] = plan.Multiplier.ToString(),
            ["LSFG3Mode"] = "FIXED",
        };
        foreach (var (key, value) in wanted)
        {
            if (!AllowedKeys.Contains(key)) continue;
            var el = doc.Descendants(key).FirstOrDefault();
            if (el is null || el.Value == value) continue;
            changes.Add(new SettingChange(key, el.Value, value));
            el.Value = value;
        }
        return (changes.Count == 0 ? xml : doc.ToString(SaveOptions.DisableFormatting), changes);
    }

    /// <summary>Puts back only the values that still equal what we wrote.</summary>
    public static string Revert(string xml, IEnumerable<SettingChange> changes)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return xml; }
        var any = false;
        foreach (var c in changes)
            if (doc.Descendants(c.Key).FirstOrDefault() is { } el && el.Value == c.New && c.Old is not null) { el.Value = c.Old; any = true; }
        return any ? doc.ToString(SaveOptions.DisableFormatting) : xml;
    }
}
