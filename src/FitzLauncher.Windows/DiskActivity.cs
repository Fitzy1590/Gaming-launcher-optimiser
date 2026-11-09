using System.Diagnostics;

namespace FitzLauncher.Windows;

/// <summary>How busy the drives are right now (Windows' "PhysicalDisk % Idle Time" counter, all drives together).</summary>
public sealed class DiskActivity
{
    private PerformanceCounter? _idle;
    private bool _failed;

    /// <returns>0-100, or null when Windows doesn't offer the counter.</returns>
    public double? BusyPercent()
    {
        if (_failed) return null;
        try
        {
            _idle ??= new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            return Math.Clamp(100 - _idle.NextValue(), 0, 100);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _failed = true; // counters disabled or damaged: stop trying
            return null;
        }
    }
}
