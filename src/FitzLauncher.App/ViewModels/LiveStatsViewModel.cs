using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Telemetry;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

/// <summary>
/// The live CPU / GPU / RAM row on the game panel (SPEC §18). Reads sensors only while <see cref="LiveReadout.ShouldPoll"/>
/// allows it (launcher in front, Library showing, no game, Low-impact mode off); otherwise the timer is stopped.
/// </summary>
public sealed partial class LiveStatsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _timer;
    private bool _reading;

    [ObservableProperty] private string _cpuClock = "–";
    [ObservableProperty] private string _cpuDetail = "";
    [ObservableProperty] private double _cpuMeter;
    [ObservableProperty] private string _gpuClock = "–";
    [ObservableProperty] private string _gpuDetail = "";
    [ObservableProperty] private double _gpuMeter;
    [ObservableProperty] private string _memory = "–";
    [ObservableProperty] private double _memoryMeter;
    [ObservableProperty] private string _pausedText = "";
    [ObservableProperty] private bool _isLive;

    public LiveStatsViewModel(AppServices s)
    {
        _s = s;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(LiveReadout.IntervalSeconds) };
        _timer.Tick += async (_, _) => await ReadAsync();
    }

    /// <summary>Re-checks the gate; called whenever the page, play state, window focus or Low-impact mode changes.</summary>
    public void Update(bool gameRunning, bool windowActive, bool libraryVisible, bool safeMode)
    {
        var lowImpact = _s.Settings.LowImpactMode;
        IsLive = LiveReadout.ShouldPoll(lowImpact, gameRunning, windowActive, libraryVisible, safeMode);
        PausedText = LiveReadout.PausedReason(lowImpact, gameRunning, safeMode);
        if (IsLive && !_timer.IsEnabled) { _timer.Start(); _ = ReadAsync(); }
        else if (!IsLive) _timer.Stop();
    }

    private async Task ReadAsync()
    {
        if (_reading) return; // never stack readings
        _reading = true;
        try
        {
            var (r, mem) = await Task.Run(() => (_s.Temps.Read(), MemoryCleaner.Snapshot()));
            if (!IsLive) return; // the gate closed while reading
            CpuClock = LiveReadout.Clock(r.CpuClockMhz);
            CpuDetail = Join(LiveReadout.Percent(r.CpuLoad), LiveReadout.Temp(r.CpuTempC));
            CpuMeter = LiveReadout.Meter(r.CpuLoad);
            GpuClock = LiveReadout.Clock(r.GpuClockMhz);
            GpuDetail = Join(LiveReadout.Percent(r.GpuLoad), LiveReadout.Temp(r.GpuTempC));
            GpuMeter = LiveReadout.Meter(r.GpuLoad);
            Memory = LiveReadout.Memory(mem.TotalMb, mem.AvailableMb);
            MemoryMeter = LiveReadout.MemoryPercent(mem.TotalMb, mem.AvailableMb);
        }
        finally { _reading = false; }
    }

    private static string Join(string load, string temp) => temp.Length == 0 ? $"{load} load" : $"{load} load · {temp}";
}
