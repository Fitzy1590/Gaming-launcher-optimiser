using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>One logical processor as reported by GetLogicalProcessorInformationEx.</summary>
/// <param name="Index">Bit index in the group-0 affinity mask.</param>
/// <param name="EfficiencyClass">Higher = faster core (P-core on Intel hybrid). All equal on non-hybrid CPUs.</param>
/// <param name="L3Id">Which L3 cache (≈ CCD on Ryzen) this processor shares.</param>
/// <param name="L3Bytes">Size of that L3. The V-Cache CCD on X3D chips has ~3x more.</param>
public readonly record struct LogicalProcessor(int Index, int CoreId, int EfficiencyClass, int L3Id, long L3Bytes);

public sealed record AffinityPlan(ulong Mask, string Description);

/// <summary>
/// Chooses which cores a game may use. Only pins where it is known to help:
/// dual-CCD Ryzen X3D → the V-Cache CCD (what Process Lasso users do), Intel 12th gen+ hybrid →
/// P-cores only (keeps the game's main threads off E-cores). Everything else is left alone.
/// </summary>
public static class CpuAffinityPlanner
{
    public static AffinityPlan? Plan(IReadOnlyList<LogicalProcessor> cpus, CorePinning mode)
    {
        if (cpus.Count == 0 || mode == CorePinning.Off || cpus.Any(c => c.Index >= 64)) return null;

        var classes = cpus.Select(c => c.EfficiencyClass).Distinct().ToList();
        var l3Groups = cpus.GroupBy(c => c.L3Id).ToList();
        var hybrid = classes.Count > 1;
        var asymmetricL3 = l3Groups.Count > 1 && l3Groups.Select(g => g.First().L3Bytes).Distinct().Count() > 1;

        if (mode == CorePinning.Auto)
            mode = asymmetricL3 ? CorePinning.VCacheCcd : hybrid ? CorePinning.PerformanceCores : CorePinning.Off;

        switch (mode)
        {
            case CorePinning.VCacheCcd when l3Groups.Count > 1:
            {
                var vcache = l3Groups.OrderByDescending(g => g.First().L3Bytes).First().ToList();
                if (vcache.Count == cpus.Count) return null;
                var mb = vcache[0].L3Bytes / 1024 / 1024;
                return new AffinityPlan(Mask(vcache), $"V-Cache CCD only ({vcache.Select(c => c.CoreId).Distinct().Count()} cores, {mb} MB L3)");
            }
            case CorePinning.PerformanceCores when hybrid:
            {
                var top = classes.Max();
                var pcores = cpus.Where(c => c.EfficiencyClass == top).ToList();
                // Too few P-threads would starve modern games; leave those CPUs alone.
                if (pcores.Count < 8) return null;
                return new AffinityPlan(Mask(pcores), $"P-cores only ({pcores.Select(c => c.CoreId).Distinct().Count()} cores / {pcores.Count} threads)");
            }
            default:
                return null;
        }
    }

    private static ulong Mask(IEnumerable<LogicalProcessor> cpus) => cpus.Aggregate(0UL, (m, c) => m | (1UL << c.Index));
}
