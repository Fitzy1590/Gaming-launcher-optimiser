using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Saves;
using FitzLauncher.Core.Shaders;
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
    [ObservableProperty] private string _sensorSource = "";
    [ObservableProperty] private string _driverChangedText = "";
    [ObservableProperty] private string _shaderStatus = "";

    public ObservableCollection<ShaderCacheRow> ShaderCaches { get; } = [];
    public HardwareProfile? Profile => _s.Hardware;
    public bool HasDriverChange => DriverChangedText.Length > 0;

    partial void OnDriverChangedTextChanged(string value) => OnPropertyChanged(nameof(HasDriverChange));

    private static (string local, string low) CacheRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return (local, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"));
    }

    [RelayCommand]
    private async Task MeasureShaderCachesAsync()
    {
        var (local, low) = CacheRoots();
        var caches = await Task.Run(() => ShaderCacheTool.Locations(local, low));
        ShaderCaches.Clear();
        foreach (var c in caches) ShaderCaches.Add(new ShaderCacheRow(c));
        ShaderStatus = caches.Count == 0 ? "No shader caches found." : $"{caches.Sum(c => c.Bytes) / 1024.0 / 1024 / 1024:0.00} GB in {caches.Count} cache(s).";
    }

    [RelayCommand]
    private async Task ClearShaderCachesAsync()
    {
        if (MessageBox.Show("Clear the driver and DirectX shader caches?\n\nGames will rebuild them: expect some stutter for the first few minutes of each game, then smoother play. Best done right after a GPU driver update, with no games running.",
                "Clear shader caches", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        long freed = 0;
        var skipped = 0;
        foreach (var row in ShaderCaches.ToList())
        {
            var (f, s) = await Task.Run(() => ShaderCacheTool.Clear(row.Cache));
            freed += f;
            skipped += s;
        }
        await MeasureShaderCachesAsync();
        ShaderStatus = $"Freed {freed / 1024.0 / 1024:0} MB." + (skipped > 0 ? $" {skipped} file(s) in use were skipped (close games/launchers and try again)." : "");
        DriverChangedText = "";
    }

    /// <summary>Compares the GPU driver with last run's; a change suggests a shader-cache clean.</summary>
    private void CheckDriverChange()
    {
        var current = _s.Hardware?.PrimaryGpu?.DriverVersion;
        if (string.IsNullOrEmpty(current)) return;
        var last = _s.Settings.LastSeenDriverVersion;
        if (last is not null && last != current)
            DriverChangedText = $"GPU driver updated ({last} → {current}). Clearing the old shader caches now avoids stale-cache stutter.";
        _s.Settings.LastSeenDriverVersion = current;
        _s.SaveSettings();
    }

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
        if (_s.Hardware is null || _s.Hardware.ScanVersion < HardwareProfile.CurrentScanVersion ||
            DateTimeOffset.Now - _s.Hardware.ScannedAt > TimeSpan.FromDays(7)) await ScanAsync();
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
        SensorSource = $"Sensors: {_s.Temps.Source}";
        SensorHint = _s.Temps.CpuTemperatureAvailable ? ""
            : _s.Temps.Source.StartsWith("MSI Afterburner", StringComparison.Ordinal)
                ? "No CPU temperature from Afterburner: enable \"CPU temperature\" in Afterburner → Settings → Monitoring, or install the PawnIO driver (pawnio.eu)."
                : "CPU temperature needs the free PawnIO driver (pawnio.eu), or MSI Afterburner running with CPU temperature monitoring on.";
    }

    private void Populate()
    {
        CheckDriverChange();
        _ = MeasureShaderCachesAsync();
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
            Facts.Add(new($"Drive {d.Letter}", $"{d.Kind switch { DriveKind.NvmeSsd => "NVMe SSD", DriveKind.SataSsd => "SATA SSD", DriveKind.Hdd => "Hard drive", DriveKind.SdCard => "SD card", _ => "Drive" }}  ·  {d.FreeBytes / 1e9:0} GB free of {d.TotalBytes / 1e9:0} GB"));
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
        if (hw.SystemModel.Length > 0) Facts.Add(new("Device", $"{hw.SystemManufacturer} {hw.SystemModel}".Trim() + (hw.IsApu ? "  ·  APU (shared CPU/GPU power & memory)" : "")));

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

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        InitDestinations();
    }

    public string ServicesText { get => Lines(G.Services); set { G.Services = Split(value); Save(); } }
    public string SuspendText { get => Lines(G.SuspendProcesses); set { G.SuspendProcesses = Split(value); Save(); } }
    public string CloseText { get => Lines(G.CloseProcesses); set { G.CloseProcesses = Split(value); Save(); } }
    public string LowPriorityText { get => Lines(G.LowPriorityProcesses); set { G.LowPriorityProcesses = Split(value); Save(); } }
    public string DataFolder => _s.Paths.Root;
    public ThermalGuardOptions T => _s.Settings.ThermalGuard;
    public BackupOptions B => _s.Settings.Backups;
    public ObservableCollection<DestinationRow> Destinations { get; } = [];
    [ObservableProperty] private string _backupStatus = "";
    [ObservableProperty] private string _startupStatus = "";

    public void InitDestinations()
    {
        Destinations.Clear();
        foreach (var d in B.Destinations) Destinations.Add(new DestinationRow(d, BackupDestinations.Resolve([d]).Count > 0));
    }

    [RelayCommand]
    private void AddGoogleDrive()
    {
        var drive = BackupDestinations.FindGoogleDrive();
        if (drive is null)
        {
            BackupStatus = "Google Drive for desktop wasn't found. Install it from google.com/drive/download and sign in; it adds a \"My Drive\" folder that syncs automatically. Then click this again.";
            return;
        }
        AddDestination(new BackupDestination { Kind = BackupDestinationKind.GoogleDrive, Path = drive, Label = "Google Drive" },
            $"Backups will be copied to {drive}\\{SaveBackupService.DestinationFolder}, and Google Drive syncs them to the cloud.");
    }

    [RelayCommand]
    private void AddUsb()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder on your USB drive for save backups" };
        if (dlg.ShowDialog() != true) return;
        var root = Path.GetPathRoot(dlg.FolderName);
        var info = root is null ? null : new DriveInfo(root);
        if (info is null || info.DriveType is not (DriveType.Removable or DriveType.Fixed))
        {
            BackupStatus = "Pick a folder on a USB drive.";
            return;
        }
        try
        {
            var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? $"USB {root}" : info.VolumeLabel;
            AddDestination(BackupDestinations.AddUsb(dlg.FolderName, label),
                $"Added {label}. It's recognised by a small hidden marker file, so a different drive letter next time is fine. When it's unplugged, backups catch up the next time it's connected.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { BackupStatus = "Couldn't write to that drive: " + e.Message; }
    }

    [RelayCommand]
    private void AddBackupFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extra folder for save backups (NAS, OneDrive, another disk…)" };
        if (dlg.ShowDialog() != true) return;
        AddDestination(new BackupDestination { Kind = BackupDestinationKind.Folder, Path = dlg.FolderName, Label = dlg.FolderName }, $"Backups will also be copied to {dlg.FolderName}.");
    }

    [RelayCommand]
    private void RemoveDestination(DestinationRow row)
    {
        B.Destinations.Remove(row.Destination);
        Save();
        InitDestinations();
    }

    private void AddDestination(BackupDestination d, string message)
    {
        B.Destinations.Add(d);
        Save();
        InitDestinations();
        BackupStatus = message;
    }

    public bool NoUacShortcut
    {
        get => S.NoUacShortcut;
        set
        {
            var exe = Environment.ProcessPath!;
            if (value)
            {
                var (ok, msg) = StartupManager.EnableNoUac(exe);
                S.NoUacShortcut = ok;
                StartupStatus = msg;
            }
            else { StartupManager.DisableNoUac(); S.NoUacShortcut = false; StartupStatus = "Removed the no-prompt shortcut."; }
            Save();
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            var (ok, msg) = StartupManager.SetStartWithWindows(value, Environment.ProcessPath!);
            S.StartWithWindows = value && ok;
            StartupStatus = msg;
            Save();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void PickDllFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with newer nvngx_dlss.dll / libxess.dll / amd_fidelityfx_dx12.dll files" };
        if (dlg.ShowDialog() != true) return;
        S.UpscalerDllFolder = dlg.FolderName;
        Save();
        OnPropertyChanged(nameof(S));
    }

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

public sealed class ShaderCacheRow(ShaderCache c)
{
    public ShaderCache Cache => c;
    public string Name => c.Name;
    public string Size => c.Bytes >= 1024L * 1024 * 1024 ? $"{c.Bytes / 1024.0 / 1024 / 1024:0.00} GB" : $"{c.Bytes / 1024.0 / 1024:0} MB";
    public string Path => c.Path;
}

public sealed class DestinationRow(BackupDestination d, bool available)
{
    public BackupDestination Destination => d;
    public string Label => d.Label;
    public string Kind => d.Kind switch { BackupDestinationKind.GoogleDrive => "Google Drive", BackupDestinationKind.Usb => "USB drive", _ => "Folder" };
    public string State => available ? "connected" : d.Kind == BackupDestinationKind.Usb ? "not plugged in: catches up when connected" : "not available";
    public bool Available => available;
}
