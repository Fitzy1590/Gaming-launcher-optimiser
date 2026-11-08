using LibreHardwareMonitor.Hardware;

namespace FitzLauncher.Windows;

public readonly record struct SensorReading(
    double? CpuTempC, double? GpuTempC, double? GpuHotspotC, double? CpuLoad, double? GpuLoad, double? GpuPowerW, string? CpuName, string? GpuName, double? CpuPowerW = null)
{
    /// <summary>Best "how much power is the chip drawing" figure: CPU package (the whole APU on handhelds), else GPU.</summary>
    public double? Watts => CpuPowerW ?? GpuPowerW;
}

/// <summary>
/// CPU/GPU sensors with no double polling:
/// 1. If MSI Afterburner is running, its published readings are used (no sensor access by us).
/// 2. Anything Afterburner doesn't provide comes from LibreHardwareMonitor, opened only for the
///    hardware that's still missing (e.g. CPU only). CPU temperatures need the PawnIO driver.
/// Readings are taken on demand (every 2 s while playing), so the cost is a few milliseconds.
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private readonly Lock _lock = new();
    private Computer? _cpu, _gpu;
    private bool _cpuFailed, _gpuFailed;

    public Func<bool> PreferAfterburner { get; set; } = () => true;
    public bool CpuTemperatureAvailable { get; private set; } = true;
    /// <summary>Where the last reading came from, for the UI.</summary>
    public string Source { get; private set; } = "";

    public SensorReading Read()
    {
        lock (_lock)
        {
            var ab = PreferAfterburner() ? AfterburnerReader.TryRead() : null;
            double? cpuT = ab?.CpuTempC, gpuT = ab?.GpuTempC, hot = ab?.GpuHotspotC, cpuL = ab?.CpuLoad, gpuL = ab?.GpuLoad, power = ab?.GpuPowerW;
            string? cpuName = null, gpuName = null;
            double? cpuPower = null;

            if (gpuT is null)
            {
                var g = ReadLhm(ref _gpu, ref _gpuFailed, gpu: true);
                gpuT = g.GpuTempC; hot = g.GpuHotspotC; gpuL ??= g.GpuLoad; power ??= g.GpuPowerW; gpuName = g.GpuName;
            }
            if (cpuT is null)
            {
                var c = ReadLhm(ref _cpu, ref _cpuFailed, gpu: false);
                cpuT = c.CpuTempC; cpuL ??= c.CpuLoad; cpuName = c.CpuName; cpuPower = c.CpuPowerW;
            }

            CpuTemperatureAvailable = cpuT is > 0;
            Source = ab is null ? "LibreHardwareMonitor"
                   : ab.Value.CpuTempC is null ? "MSI Afterburner (GPU) + LibreHardwareMonitor (CPU)"
                   : "MSI Afterburner";
            return new SensorReading(cpuT is > 0 ? cpuT : null, gpuT, hot, cpuL, gpuL, power, cpuName, gpuName, cpuPower);
        }
    }

    private static SensorReading ReadLhm(ref Computer? computer, ref bool failed, bool gpu)
    {
        if (failed) return default;
        if (computer is null)
        {
            var c = new Computer { IsCpuEnabled = !gpu, IsGpuEnabled = gpu };
            try { c.Open(); }
            catch (Exception) { failed = true; return default; } // driver blocked / not admin
            computer = c;
        }

        double? cpuTemp = null, gpuTemp = null, hotspot = null, cpuLoad = null, gpuLoad = null, gpuPower = null, cpuPower = null;
        string? cpuName = null, gpuName = null;
        foreach (var hw in computer.Hardware)
        {
            try { hw.Update(); } catch (Exception) { continue; }
            if (hw.HardwareType == HardwareType.Cpu)
            {
                cpuName ??= hw.Name;
                // Intel: "CPU Package"; AMD: "Core (Tctl/Tdie)"; otherwise the hottest core.
                cpuTemp ??= Sensor(hw, SensorType.Temperature, "CPU Package") ??
                            Sensor(hw, SensorType.Temperature, "Core (Tctl/Tdie)") ??
                            Sensor(hw, SensorType.Temperature, "Tctl") ??
                            Max(hw, SensorType.Temperature);
                cpuLoad ??= Sensor(hw, SensorType.Load, "CPU Total");
                cpuPower ??= Sensor(hw, SensorType.Power, "Package") ?? Sensor(hw, SensorType.Power, "CPU Package");
            }
            else if (hw.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal))
            {
                // Prefer the discrete GPU (the one reporting the higher load) when there are two.
                var load = Sensor(hw, SensorType.Load, "GPU Core") ?? Sensor(hw, SensorType.Load, "D3D 3D");
                if (gpuName is not null && (load ?? 0) <= (gpuLoad ?? 0)) continue;
                gpuName = hw.Name;
                gpuLoad = load;
                gpuTemp = Sensor(hw, SensorType.Temperature, "GPU Core") ?? Max(hw, SensorType.Temperature);
                hotspot = Sensor(hw, SensorType.Temperature, "GPU Hot Spot");
                gpuPower = Sensor(hw, SensorType.Power, "GPU Package") ?? Sensor(hw, SensorType.Power, "GPU Power");
            }
        }
        return new SensorReading(cpuTemp, gpuTemp, hotspot, cpuLoad, gpuLoad, gpuPower, cpuName, gpuName, cpuPower);
    }

    private static double? Sensor(IHardware hw, SensorType type, string name)
    {
        var s = hw.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return s?.Value is { } v && v > 0 ? v : null;
    }

    private static double? Max(IHardware hw, SensorType type)
    {
        var values = hw.Sensors.Where(s => s.SensorType == type && s.Value is > 0).Select(s => (double)s.Value!.Value).ToList();
        return values.Count == 0 ? null : values.Max();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cpu?.Close();
            _gpu?.Close();
            _cpu = _gpu = null;
        }
    }
}
