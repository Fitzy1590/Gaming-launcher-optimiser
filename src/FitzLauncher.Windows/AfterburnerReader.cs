using System.IO.MemoryMappedFiles;
using System.Text;

namespace FitzLauncher.Windows;

/// <summary>
/// Reads MSI Afterburner's monitoring shared memory (MAHMSharedMemory), which Afterburner publishes
/// for other apps. When Afterburner is running, the launcher uses its readings instead of polling
/// the same sensors itself, so two tools never fight over the SMBus/driver.
/// </summary>
public static class AfterburnerReader
{
    private const uint Signature = 0x4D41484D; // 'MAHM'
    private const float Unavailable = float.MaxValue;

    public readonly record struct Readings(double? GpuTempC, double? CpuTempC, double? GpuLoad, double? CpuLoad, double? GpuPowerW, double? GpuHotspotC);

    public static Readings? TryRead()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != Signature) return null; // 0xDEAD while Afterburner is closing
            var headerSize = v.ReadUInt32(8);
            var count = v.ReadUInt32(12);
            var entrySize = v.ReadUInt32(16);
            if (count == 0 || entrySize < 1316) return null;

            double? gpuT = null, cpuT = null, gpuL = null, cpuL = null, power = null, hotspot = null;
            for (uint i = 0; i < count; i++)
            {
                long e = headerSize + i * entrySize;
                var name = ReadString(v, e, 260); // szSrcName (English, not localised)
                var value = v.ReadSingle(e + 1300);
                if (value == Unavailable || float.IsNaN(value)) continue;
                var n = name.ToLowerInvariant();
                // "GPU temperature" / "GPU1 temperature"; first GPU wins.
                if (n.StartsWith("gpu") && n.EndsWith(" temperature") && !n.Contains("mem") && !n.Contains("hot") && !n.Contains("vrm")) gpuT ??= value;
                else if (n.StartsWith("gpu") && n.Contains("hot spot")) hotspot ??= value;
                else if (n.StartsWith("gpu") && n.EndsWith(" usage") && !n.Contains("memory") && !n.Contains("fb")) gpuL ??= value;
                else if (n is "cpu temperature") cpuT = value;
                else if (n is "cpu usage") cpuL = value;
                else if (n is "power" or "gpu power" or "gpu1 power") power ??= value;
            }
            return gpuT is null && cpuT is null ? null : new Readings(gpuT, cpuT, gpuL, cpuL, power, hotspot);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ReadString(MemoryMappedViewAccessor v, long offset, int max)
    {
        var bytes = new byte[max];
        v.ReadArray(offset, bytes, 0, max);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? max : end).Trim();
    }
}
