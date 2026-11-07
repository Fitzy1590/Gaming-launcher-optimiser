using LibreHardwareMonitor.Hardware;

namespace FitzLauncher.Windows;

public readonly record struct SensorReading(
    double? CpuTempC, double? GpuTempC, double? GpuHotspotC, double? CpuLoad, double? GpuLoad, double? GpuPowerW, string? CpuName, string? GpuName);

/// <summary>
/// Thin wrapper over LibreHardwareMonitor. Only CPU and GPU are opened (no motherboard/storage
/// polling) and sensors are read on demand, so a reading costs a few milliseconds every couple of
/// seconds. CPU temperatures need the PawnIO driver (pawnio.eu); GPU temperatures work without it.
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Lock _lock = new();
    private bool _opened;

    public TemperatureMonitor()
    {
        _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
    }

    public bool CpuTemperatureAvailable { get; private set; } = true;

    public SensorReading Read()
    {
        lock (_lock)
        {
            if (!_opened)
            {
                try { _computer.Open(); }
                catch (Exception) { return default; } // driver blocked / not admin
                _opened = true;
            }

            double? cpuTemp = null, gpuTemp = null, hotspot = null, cpuLoad = null, gpuLoad = null, gpuPower = null;
            string? cpuName = null, gpuName = null;
            foreach (var hw in _computer.Hardware)
            {
                try { hw.Update(); } catch (Exception) { continue; }
                var isGpu = hw.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal);
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    cpuName ??= hw.Name;
                    // Intel: "CPU Package"; AMD: "Core (Tctl/Tdie)"; otherwise the hottest core.
                    cpuTemp ??= Sensor(hw, SensorType.Temperature, "CPU Package") ??
                                Sensor(hw, SensorType.Temperature, "Core (Tctl/Tdie)") ??
                                Sensor(hw, SensorType.Temperature, "Tctl") ??
                                Max(hw, SensorType.Temperature);
                    cpuLoad ??= Sensor(hw, SensorType.Load, "CPU Total");
                }
                else if (isGpu)
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
            if (cpuTemp is null or <= 0) { cpuTemp = null; CpuTemperatureAvailable = false; }
            else CpuTemperatureAvailable = true;
            return new SensorReading(cpuTemp, gpuTemp, hotspot, cpuLoad, gpuLoad, gpuPower, cpuName, gpuName);
        }
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
            if (_opened) _computer.Close();
            _opened = false;
        }
    }
}
