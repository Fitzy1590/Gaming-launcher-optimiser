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

    private OverlayEditorViewModel? _overlayEditor;
    /// <summary>The default in-game overlay layout.</summary>
    public OverlayEditorViewModel OverlayEditor => _overlayEditor ??= new OverlayEditorViewModel(_s.Settings.EffectiveOverlay, _s.SaveSettings);
    // ---- game help (AI Game Companion)
    public bool GameHelpWithGames { get => _s.Settings.GameHelpWithGames; set { _s.Settings.GameHelpWithGames = value; _s.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(GameHelpText)); } }
    public string GameHelpText => _s.GameHelp.Locate() is { } p
        ? $"Found: {p}. PS + Triangle shows its panel in game (Ctrl+Shift+G); PS + another button can be set to \"ask about the screen\" (Ctrl+Shift+A)."
        : "Not installed. Get it from its GitHub releases (free, MIT), set your AI provider in its Settings, then Choose… its launcher.exe if it isn't found.";

    [RelayCommand] private static void GetGameHelp() => ModsViewModel.Open(FitzLauncher.App.Services.GameHelpService.ReleasesUrl);

    [RelayCommand]
    private void ChooseGameHelp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Find AI Game Companion (launcher.exe)", Filter = "launcher.exe|launcher.exe|Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.GameHelpPath = dlg.FileName;
        _s.SaveSettings();
        OnPropertyChanged(nameof(GameHelpText));
    }

    /// <summary>Auto TDP is for handhelds (Ryzen APUs): the section only shows there.</summary>
    public bool ShowAutoTdp => _s.Hardware?.IsApu == true;

    [RelayCommand]
    private void OpenAutoTdpFolder()
    {
        var dir = Path.Combine(_s.Paths.Root, "auto-tdp");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public int HibernateMinutes
    {
        get => _s.Settings.HibernateAfterMinutes;
        set { _s.Settings.HibernateAfterMinutes = value; OnPropertyChanged(); OnPropertyChanged(nameof(HibernateLabel)); }
    }
    public string HibernateLabel => HibernateMinutes == 0 ? "Never" : $"{HibernateMinutes} min";
    [ObservableProperty] private string _hibernateText = "";

    [RelayCommand]
    private async Task ApplyHibernateAsync()
    {
        _s.SaveSettings();
        var minutes = HibernateMinutes;
        HibernateText = await Task.Run(() => FitzLauncher.Windows.LowPowerSleep.SetHibernateAfter(minutes));
    }

    public ControllersViewModel Controllers { get; }
    public GameModeOptions G => _s.Settings.GameMode;
    public int[] Targets => LauncherSettings.FpsTargets;

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        InitDestinations();
        _ = LoadIdleTimeoutsAsync();
        Controllers = new ControllersViewModel(s);
        LoadSnapshots();
        _ = Controllers.RefreshAsync();
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

    // ------------------------------------------------------------------ power (RyzenAdj)

    [ObservableProperty] private string _powerStatus = "";
    public string RyzenAdjText => RyzenAdjClient.Find(S.RyzenAdjPath) is { } p ? p : "Not found";

    [RelayCommand]
    private void PickRyzenAdj()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Find ryzenadj.exe", Filter = "ryzenadj.exe|ryzenadj.exe|Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog() != true) return;
        S.RyzenAdjPath = dlg.FileName;
        Save();
        OnPropertyChanged(nameof(RyzenAdjText));
    }

    [RelayCommand]
    private async Task TestPowerAsync()
    {
        if (RyzenAdjClient.Find(S.RyzenAdjPath) is not { } path) { PowerStatus = "RyzenAdj not found. Download it from github.com/FlyGoat/RyzenAdj (releases), unzip, then Browse to ryzenadj.exe."; return; }
        PowerStatus = "Reading…";
        var limits = await Task.Run(() => new RyzenAdjClient(path, _s.Paths).Read());
        PowerStatus = limits is null
            ? "RyzenAdj ran but couldn't read the limits. This CPU may not be supported by your RyzenAdj version yet."
            : $"Working. Current limits: {limits.Text}.";
    }

    // ------------------------------------------------------------------ snapshots

    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];
    [ObservableProperty] private string _snapshotStatus = "";

    public void LoadSnapshots()
    {
        Snapshots.Clear();
        foreach (var sn in FitzLauncher.Core.Storage.LibrarySnapshots.List(_s.Paths).Take(14)) Snapshots.Add(new SnapshotRow(sn, RestoreSnapshot));
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        _s.SaveLibrary();
        _s.SaveSettings();
        var sn = await Task.Run(() => FitzLauncher.Core.Storage.LibrarySnapshots.Take(_s.Paths));
        SnapshotStatus = sn is null ? "Nothing to snapshot yet." : "Snapshot taken.";
        LoadSnapshots();
    }

    private void RestoreSnapshot(FitzLauncher.Core.Storage.SnapshotInfo sn)
    {
        if (MessageBox.Show($"Restore your library and settings from {sn.Label}?\n\nThe current state is snapshotted first, so this can be undone. The launcher restarts.",
                "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            FitzLauncher.Core.Storage.LibrarySnapshots.Restore(_s.Paths, sn);
            AppServices.SkipSaveOnExit = true;
            if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SnapshotStatus = $"Couldn't restore: {e.Message}";
        }
    }

    // ------------------------------------------------------------------ mods & look

    [ObservableProperty] private string _nexusKeyInput = "";
    [ObservableProperty] private string _modIoKeyInput = "";
    [ObservableProperty] private string _modsStatus = "";
    public bool HasNexusKey => _s.Settings.NexusApiKeyProtected is not null;
    public bool HasModIoKey => _s.Settings.ModIoApiKeyProtected is not null;

    [RelayCommand]
    private async Task SaveNexusKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusKeyInput)) { _s.Settings.NexusApiKeyProtected = null; _s.SaveSettings(); ModsStatus = "Nexus key removed."; OnPropertyChanged(nameof(HasNexusKey)); return; }
        var client = new FitzLauncher.App.Services.Mods.NexusClient(_s.StoreHttp, NexusKeyInput.Trim(), _s.Paths.Root);
        ModsStatus = "Checking the key with Nexus…";
        try
        {
            var user = await client.ValidateAsync();
            _s.Settings.NexusApiKeyProtected = FitzLauncher.App.Services.Mods.Secrets.Protect(NexusKeyInput);
            _s.SaveSettings();
            NexusKeyInput = "";
            ModsStatus = $"Connected to Nexus as {user.Name} ({(user.IsPremium ? "Premium: installs download directly" : "free account: installs go through the site's Mod Manager Download button")}).";
        }
        catch (Exception e) when (e is FitzLauncher.App.Services.Mods.StoreException or System.Net.Http.HttpRequestException or TaskCanceledException) { ModsStatus = e.Message; }
        OnPropertyChanged(nameof(HasNexusKey));
    }

    [RelayCommand]
    private void SaveModIoKey()
    {
        _s.Settings.ModIoApiKeyProtected = FitzLauncher.App.Services.Mods.Secrets.Protect(ModIoKeyInput);
        _s.SaveSettings();
        ModIoKeyInput = "";
        ModsStatus = HasModIoKey ? "mod.io key saved." : "mod.io key removed.";
        OnPropertyChanged(nameof(HasModIoKey));
    }

    [RelayCommand] private static void OpenNexusKeyPage() => OpenUri("https://www.nexusmods.com/settings/api-keys");
    [RelayCommand] private static void OpenModIoKeyPage() => OpenUri("https://mod.io/me/access");

    /// <summary>Nexus "Mod Manager Download" links open in the launcher (previous handler restored when off).</summary>
    public bool NxmHandler
    {
        get => _s.Settings.NxmHandler;
        set
        {
            try
            {
                if (value) _s.Settings.NxmPreviousCommand = FitzLauncher.App.Services.Mods.NxmHandler.Register() ?? _s.Settings.NxmPreviousCommand;
                else FitzLauncher.App.Services.Mods.NxmHandler.Unregister(_s.Settings.NxmPreviousCommand);
                _s.Settings.NxmHandler = value;
                ModsStatus = value ? "Nexus links now open in the launcher." : "Nexus links handed back" + (_s.Settings.NxmPreviousCommand is null ? "." : " to your previous mod manager.");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { ModsStatus = $"Couldn't change the link handler: {e.Message}"; }
            _s.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool MatchGameColours
    {
        get => _s.Settings.MatchGameColours;
        set
        {
            _s.Settings.MatchGameColours = value;
            FitzLauncher.App.Services.GameTheme.Enabled = value;
            if (!value) FitzLauncher.App.Services.GameTheme.Apply(FitzLauncher.Core.Artwork.ArtPalette.Default);
            _s.SaveSettings();
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------ sleep

    [ObservableProperty] private string _idleTimeoutsText = "Reading Windows' sleep settings…";

    private async Task LoadIdleTimeoutsAsync()
    {
        var t = await Task.Run(PowerPlans.GetIdleTimeouts);
        static string M(int? m) => m switch { null => "?", 0 => "never", _ => $"{m} min" };
        IdleTimeoutsText = t.screenAc is null && t.screenDc is null
            ? "Couldn't read Windows' sleep settings."
            : $"Windows turns the screen off after {M(t.screenDc)} on battery ({M(t.screenAc)} plugged in) and sleeps after {M(t.sleepDc)} ({M(t.sleepAc)} plugged in).";
    }

    [RelayCommand] private static void OpenSleepSettings() => OpenUri("ms-settings:powersleep");
    [RelayCommand] private static void OpenSignInSettings() => OpenUri("ms-settings:signinoptions");

    private static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // ------------------------------------------------------------------ undervolt

    public string UndervoltText => !S.UndervoltEnabled ? "Off."
        : S.UndervoltCurrent == 0 ? $"Starts at -{UndervoltLadder.Step} next game, then steps toward {S.UndervoltTarget} after each clean session."
        : S.UndervoltSettled ? $"Settled at {S.UndervoltCurrent}{(S.UndervoltCurrent > S.UndervoltTarget ? " (backed off after a crash)" : "")}."
        : $"Now {S.UndervoltCurrent}, stepping toward {S.UndervoltTarget}.";

    [RelayCommand]
    private void ResetUndervolt()
    {
        S.UndervoltCurrent = 0;
        S.UndervoltSettled = false;
        _s.SaveSettings();
        OnPropertyChanged(nameof(UndervoltText));
    }

    // ------------------------------------------------------------------ sync

    [ObservableProperty] private string _syncStatus = "";
    public string SyncFolderText => S.SyncFolder ?? BackupDestinations.FindGoogleDrive() ?? "Google Drive for desktop not found";

    [RelayCommand]
    private void PickSyncFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to sync your library through (e.g. G:\\My Drive)" };
        if (dlg.ShowDialog() != true) return;
        S.SyncFolder = dlg.FolderName;
        Save();
        OnPropertyChanged(nameof(SyncFolderText));
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (_s.Sync is null) { SyncStatus = "Turn sync on and make sure the folder exists (Google Drive for desktop, or pick a folder)."; return; }
        await Task.Run(_s.SyncUpload);
        SyncStatus = $"Synced at {DateTime.Now:HH:mm}.";
    }

    [RelayCommand]
    private void RestoreFromSync()
    {
        if (_s.Sync is not { } sync || sync.Remote() is not { } remote) { SyncStatus = "Nothing in the sync folder yet."; return; }
        if (System.Windows.MessageBox.Show($"Replace this PC's library, settings and history with the synced copy from {remote.Machine} ({remote.Updated.ToLocalTime():d MMM HH:mm})?\n\nYour current data is copied to backups first. The launcher restarts.",
                "Restore from sync", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        sync.Download();
        RestartApp();
    }

    public static void RestartApp()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ updates

    public Action<FitzLauncher.Core.Updates.ReleaseInfo>? ShowUpdate { get; init; }
    public Func<Task>? InstallUpdate { get; init; }
    public string CurrentVersion => $"Version {UpdateService.CurrentVersion}";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private string _tokenInput = "";
    public bool HasToken => _s.Settings.GitHubTokenProtected is not null;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Checking…";
        var r = await _s.Updates.CheckAsync();
        UpdateStatus = r.Message;
        UpdateReady = r.Available;
        if (r.Available) ShowUpdate?.Invoke(r.Release!);
    }

    /// <summary>Downloads and reinstalls the latest release even if it's the version you have (repairs missing or damaged files).</summary>
    [RelayCommand]
    private async Task ReinstallLatestAsync()
    {
        if (MessageBox.Show("Download the latest release and reinstall it over this copy? The launcher restarts; your library, settings and backups are kept.",
                "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UpdateStatus = "Finding the latest release…";
        var r = await _s.Updates.CheckAsync();
        if (r.Release is null) { UpdateStatus = r.Message; return; }
        UpdateStatus = $"Reinstalling {r.Release.Version}…";
        ShowUpdate?.Invoke(r.Release);
        if (InstallUpdate is not null) await InstallUpdate();
    }

    [RelayCommand]
    private async Task InstallUpdateNowAsync()
    {
        if (InstallUpdate is not null) await InstallUpdate();
    }

    [RelayCommand]
    private async Task SaveTokenAsync()
    {
        _s.Updates.Token = TokenInput;
        TokenInput = "";
        OnPropertyChanged(nameof(HasToken));
        UpdateStatus = HasToken ? "Token saved (encrypted with your Windows account). Checking…" : "Token removed.";
        if (HasToken) await CheckUpdatesAsync();
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

public sealed class SnapshotRow(FitzLauncher.Core.Storage.SnapshotInfo info, Action<FitzLauncher.Core.Storage.SnapshotInfo> restore)
{
    public string Label => info.Label + (info.Path.Contains("before-restore") ? " · before a restore" : "");
    public CommunityToolkit.Mvvm.Input.IRelayCommand RestoreCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => restore(info));
}
