using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

public sealed record Fact(string Label, string Value);

public sealed partial class HardwareViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _sensorTimer;

    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<TweakViewModel> Recommendations { get; } = [];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scannedText = "";
    [ObservableProperty] private string _cpuTemp = "–";
    [ObservableProperty] private string _gpuTemp = "–";
    [ObservableProperty] private string _cpuLoad = "–";
    [ObservableProperty] private string _gpuLoad = "–";
    [ObservableProperty] private string _memory = "–";
    [ObservableProperty] private string _sensorHint = "";

    public HardwareProfile? Profile => _s.Hardware;

    public HardwareViewModel(AppServices s)
    {
        _s = s;
        _sensorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _sensorTimer.Tick += async (_, _) => await ReadSensorsAsync();
        if (s.Hardware is not null) Populate();
    }

    /// <summary>Scan on first run, or when the cached profile is a week old.</summary>
    public async Task EnsureScannedAsync()
    {
        if (_s.Hardware is null || DateTimeOffset.Now - _s.Hardware.ScannedAt > TimeSpan.FromDays(7)) await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            _s.Hardware = await Task.Run(HardwareScanner.Scan);
            _s.Store.SaveHardware(_s.Hardware);
            Populate();
        }
        finally { IsScanning = false; }
    }

    public void SetVisible(bool visible)
    {
        if (visible) { _sensorTimer.Start(); _ = ReadSensorsAsync(); }
        else _sensorTimer.Stop();
    }

    private async Task ReadSensorsAsync()
    {
        var (r, mem) = await Task.Run(() => (_s.Temps.Read(), MemoryCleaner.Snapshot()));
        CpuTemp = r.CpuTempC is { } c ? $"{c:0} °C" : "n/a";
        GpuTemp = r.GpuTempC is { } g ? $"{g:0} °C" + (r.GpuHotspotC is { } h ? $"  (hotspot {h:0})" : "") : "n/a";
        CpuLoad = r.CpuLoad is { } cl ? $"{cl:0}%" : "–";
        GpuLoad = r.GpuLoad is { } gl ? $"{gl:0}%" + (r.GpuPowerW is { } w ? $"  ·  {w:0} W" : "") : "–";
        Memory = $"{(mem.TotalMb - mem.AvailableMb) / 1024.0:0.0} / {mem.TotalMb / 1024.0:0.0} GB used  ·  {mem.StandbyMb / 1024.0:0.0} GB standby";
        SensorHint = !_s.Temps.CpuTemperatureAvailable
            ? "CPU temperature needs the free PawnIO driver (pawnio.eu). Install it once and restart the launcher."
            : "";
    }

    private void Populate()
    {
        var hw = _s.Hardware!;
        ScannedText = $"Scanned {hw.ScannedAt:d MMM yyyy, HH:mm}";
        Facts.Clear();
        Facts.Add(new("CPU", $"{hw.CpuName}  ·  {hw.CpuCores} cores / {hw.CpuThreads} threads" + (hw.CpuHasVCache ? "  ·  3D V-Cache" : "") + (hw.CpuIsHybrid ? "  ·  P+E cores" : "")));
        foreach (var g in hw.Gpus)
            Facts.Add(new(g.IsDiscrete ? "GPU" : "iGPU", $"{g.Name}  ·  {g.VramGb:0.#} GB  ·  driver {g.DriverVersion}" + (g.DriverDate is { } d ? $" ({d:MMM yyyy})" : "")));
        Facts.Add(new("Memory", $"{hw.RamGb:0} GB {hw.RamType}  ·  {hw.RamConfiguredMhz} MT/s  ·  {hw.RamSticks} stick(s)"));
        foreach (var d in hw.Displays)
            Facts.Add(new(d.IsPrimary ? "Display" : "Display 2", $"{d.Width}×{d.Height} @ {d.CurrentRefreshHz} Hz" + (d.MaxRefreshHz > d.CurrentRefreshHz ? $" (supports {d.MaxRefreshHz} Hz)" : "")));
        foreach (var d in hw.Drives)
            Facts.Add(new($"Drive {d.Letter}", $"{d.Kind switch { DriveKind.NvmeSsd => "NVMe SSD", DriveKind.SataSsd => "SATA SSD", DriveKind.Hdd => "Hard drive", _ => "Drive" }}  ·  {d.FreeBytes / 1e9:0} GB free of {d.TotalBytes / 1e9:0} GB"));
        Facts.Add(new("Windows", $"{hw.OsName} (build {hw.OsBuild})  ·  power plan: {hw.ActivePowerPlan}"));
        Facts.Add(new("Settings", string.Join("  ·  ", new[]
        {
            $"Game Mode {OnOff(hw.WindowsGameModeEnabled)}",
            $"HAGS {OnOff(hw.HagsEnabled)}",
            $"Memory integrity {OnOff(hw.MemoryIntegrityEnabled)}",
            $"Background recording {OnOff(hw.GameDvrBackgroundRecording)}",
            hw.RtssInstalled ? "RTSS installed" : "RTSS not installed",
        })));
        if (hw.IsLaptop) Facts.Add(new("Type", hw.OnBattery ? "Laptop (on battery)" : "Laptop (plugged in)"));

        Recommendations.Clear();
        foreach (var t in SystemAdvisor.Analyse(hw, _s.Library.Games).OrderByDescending(t => t.Recommended))
            Recommendations.Add(new TweakViewModel(t, ApplyAsync, RevertAsync) { IsApplied = TweakEngine.IsApplied(_s.Library.SystemChanges, t) });
        OnPropertyChanged(nameof(Profile));
    }

    private static string OnOff(bool? b) => b switch { true => "on", false => "off", _ => "n/a" };

    private async Task ApplyAsync(TweakViewModel t)
    {
        var r = await Task.Run(() => _s.Tweaks.Apply(_s.Library.SystemChanges, t.Tweak));
        t.IsApplied = r.Success;
        t.Message = r.Success ? (t.NeedsRestart ? "Applied: restart Windows to take effect" : "Applied") : r.Message;
        _s.SaveLibrary();
    }

    private async Task RevertAsync(TweakViewModel t)
    {
        var errors = await Task.Run(() => _s.Tweaks.RevertAll(_s.Library.SystemChanges, null, t.Tweak.Id));
        t.IsApplied = TweakEngine.IsApplied(_s.Library.SystemChanges, t.Tweak);
        t.Message = errors.Count == 0 ? "Reverted" : string.Join("; ", errors);
        _s.SaveLibrary();
    }
}

public sealed partial class SessionsViewModel(AppServices s) : ObservableObject
{
    public ObservableCollection<SessionRow> Rows { get; } = [];
    [ObservableProperty] private string _totals = "";

    public void Reload()
    {
        var all = s.Store.LoadSessions().OrderByDescending(x => x.Start).ToList();
        Rows.Clear();
        foreach (var x in all.Take(300)) Rows.Add(new SessionRow(x));
        var hours = all.Sum(x => x.Duration.TotalHours);
        Totals = all.Count == 0 ? "No sessions yet. Play something!" : $"{all.Count} sessions  ·  {hours:0.#} hours";
    }
}

/// <summary>Settings page. Every change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;
    public LauncherSettings S => _s.Settings;
    public GameModeOptions G => _s.Settings.GameMode;
    public int[] Targets => LauncherSettings.FpsTargets;

    public SettingsViewModel(AppServices s) => _s = s;

    public string ServicesText { get => Lines(G.Services); set { G.Services = Split(value); Save(); } }
    public string SuspendText { get => Lines(G.SuspendProcesses); set { G.SuspendProcesses = Split(value); Save(); } }
    public string CloseText { get => Lines(G.CloseProcesses); set { G.CloseProcesses = Split(value); Save(); } }
    public string LowPriorityText { get => Lines(G.LowPriorityProcesses); set { G.LowPriorityProcesses = Split(value); Save(); } }
    public string DataFolder => _s.Paths.Root;

    [RelayCommand]
    public void Save() => _s.SaveSettings();

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_s.Paths.Root}\"") { UseShellExecute = true });

    [RelayCommand]
    private void ResetGameModeLists()
    {
        var d = new GameModeOptions();
        G.Services = d.Services;
        G.SuspendProcesses = d.SuspendProcesses;
        G.CloseProcesses = d.CloseProcesses;
        G.LowPriorityProcesses = d.LowPriorityProcesses;
        Save();
        OnPropertyChanged(string.Empty);
    }

    private static string Lines(IEnumerable<string> l) => string.Join(Environment.NewLine, l);
    private static List<string> Split(string s) => s.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
