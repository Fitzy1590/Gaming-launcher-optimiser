using System.Runtime.InteropServices;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Windows;

/// <summary>
/// Reads per-core efficiency class (P vs E cores) and which L3 cache each logical processor uses
/// (CCDs on Ryzen; the V-Cache CCD has the bigger L3) via GetLogicalProcessorInformationEx.
/// </summary>
public static class CpuTopologyReader
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationAll = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);

    public static List<LogicalProcessor> Read()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationAll, 0, ref len);
        if (len == 0) return [];
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buf, ref len)) return [];
            var cores = new List<(ulong mask, int group, byte eff)>();
            var l3 = new List<(ulong mask, int group, long size)>();
            for (long off = 0; off < len;)
            {
                var p = buf + (nint)off;
                var rel = Marshal.ReadInt32(p, 0);
                var size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                if (rel == RelationProcessorCore)
                {
                    // PROCESSOR_RELATIONSHIP: Flags@8, EfficiencyClass@9, GroupCount@30, GroupMask[0]@32 {Mask, Group}
                    cores.Add(((ulong)Marshal.ReadInt64(p, 32), Marshal.ReadInt16(p, 40), Marshal.ReadByte(p, 9)));
                }
                else if (rel == RelationCache && Marshal.ReadByte(p, 8) == 3)
                {
                    // CACHE_RELATIONSHIP: Level@8, CacheSize@12, GroupMask@40 {Mask, Group}
                    l3.Add(((ulong)Marshal.ReadInt64(p, 40), Marshal.ReadInt16(p, 48), (uint)Marshal.ReadInt32(p, 12)));
                }
                off += size;
            }

            var result = new List<LogicalProcessor>();
            for (var coreId = 0; coreId < cores.Count; coreId++)
            {
                var (mask, group, eff) = cores[coreId];
                if (group != 0) continue; // >64 logical processors: leave pinning off
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((mask & (1UL << bit)) == 0) continue;
                    var cacheIndex = l3.FindIndex(c => c.group == 0 && (c.mask & (1UL << bit)) != 0);
                    var cacheSize = cacheIndex >= 0 ? l3[cacheIndex].size : 0;
                    result.Add(new LogicalProcessor(bit, coreId, eff, cacheIndex, cacheSize));
                }
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
