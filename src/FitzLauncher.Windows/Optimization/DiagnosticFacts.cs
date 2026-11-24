using System.ComponentModel;
using System.Diagnostics;

namespace FitzLauncher.Windows.Optimization;

/// <summary>Read-only snapshot of what the booster has set: timer, power plan, CPU affinity masks (SPEC §17 G3/G4).</summary>
public static class DiagnosticFacts
{
    public static string TimerText()
    {
        try
        {
            return Native.NtQueryTimerResolution(out var min, out var max, out var cur) == 0
                ? $"{cur / 10000.0:0.000} ms now (finest {max / 10000.0:0.000} ms, coarsest {min / 10000.0:0.000} ms)"
                : "not available";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return "not available"; }
    }

    public static string PowerPlanText() => PowerPlans.GetActive() is { guid: not null } p ? p.name : "unknown";

    public static List<(string Name, string Value)> AffinityMasks(IEnumerable<int> pids)
    {
        var list = new List<(string, string)>();
        foreach (var pid in pids.Take(8))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                list.Add(($"Affinity {p.ProcessName} ({pid})", $"0x{(long)p.ProcessorAffinity:X} · {p.PriorityClass}"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        return list;
    }

    public static List<(string Name, string Value)> Collect(IEnumerable<int> gamePids)
    {
        var facts = new List<(string Name, string Value)>
        {
            ("OS", Environment.OSVersion.VersionString),
            ("Logical processors", Environment.ProcessorCount.ToString()),
            ("Timer resolution", TimerText()),
            ("Power plan", PowerPlanText()),
        };
        var masks = AffinityMasks(gamePids);
        if (masks.Count == 0) facts.Add(("Game affinity", "no game running"));
        else facts.AddRange(masks);
        return facts;
    }
}
