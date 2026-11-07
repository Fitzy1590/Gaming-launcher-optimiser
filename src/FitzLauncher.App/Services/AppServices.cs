using System.Net.Http;
using FitzLauncher.Core.Artwork;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Presets;
using FitzLauncher.Core.Saves;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;
using FitzLauncher.Core.Upscalers;
using FitzLauncher.Windows;

namespace FitzLauncher.App.Services;

/// <summary>Composition root: one instance of every service, created at startup.</summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public LibraryStore Store { get; }
    public LibraryData Library { get; }
    public LauncherSettings Settings { get; }
    public HardwareProfile? Hardware { get; set; }
    public TweakEngine Tweaks { get; }
    public GameScanner Scanner { get; }
    public OptimizationPlanner Planner { get; } = new();
    public HttpClient Http { get; }
    public TemperatureMonitor Temps { get; } = new();
    public GameModeController GameMode { get; }
    public Log Log { get; }
    public bool IsElevated { get; } = Environment.IsPrivilegedProcess;
    public SaveBackupService SaveBackups { get; }
    public RedistService Redists { get; } = new();
    public SaveContext SaveContext { get; } = SaveContext.FromEnvironment();

    private readonly Lazy<IReadOnlyList<LogicalProcessor>> _topology = new(() => CpuTopologyReader.Read());
    public IReadOnlyList<LogicalProcessor> CpuTopology => _topology.Value;

    private readonly Lock _saveLock = new();

    public AppServices()
    {
        Paths = AppPaths.Default();
        Paths.EnsureCreated();
        Log = new Log(Paths.LogFile);
        Store = new LibraryStore(Paths);
        Library = Store.LoadLibrary();
        Settings = Store.LoadSettings();
        Hardware = Store.LoadHardware();
        Tweaks = new TweakEngine(new WindowsRegistryAccess(), Paths.BackupDir);
        Scanner = new GameScanner(ScanContext.FromEnvironment());
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FitzLauncher/0.1");
        GameMode = new GameModeController(Paths, Log.Write);
        SaveBackups = new SaveBackupService(Paths.SaveBackupDir);
        Temps.PreferAfterburner = () => Settings.PreferAfterburnerSensors;
    }

    /// <summary>Per-game presets plus DLL upgrades, merged into each game's plan as suggestions.</summary>
    public List<Tweak> ExtraTweaks(Game game, GameScanResult scan)
    {
        var list = PresetCatalog.For(game, scan, PresetCatalog.Load(Paths.Presets)).ToList();
        list.AddRange(UpscalerLibrary.Upgrades(scan, BestUpscalerDlls()));
        return list;
    }

    /// <summary>Newest DLSS/XeSS/FSR DLL across every scanned game plus the optional DLL folder.</summary>
    public Dictionary<UpscalerDllKind, UpscalerDllInfo> BestUpscalerDlls()
    {
        var all = Library.Games.SelectMany(g => g.LastScan?.UpscalerDlls ?? []).ToList();
        if (Settings.UpscalerDllFolder is { } dir && Directory.Exists(dir))
        {
            var folderScan = new GameScanner(ScanContext.FromEnvironment())
                .Scan(new Game { Name = "dll-folder", ExePath = Path.Combine(dir, "none.exe"), InstallDir = dir });
            all.AddRange(folderScan.UpscalerDlls);
        }
        return UpscalerLibrary.BestVersions(all);
    }

    public OptimizationPlan BuildPlan(Game game) =>
        Planner.Build(game, game.LastScan!, Hardware, game.FpsTarget, game.LastSession, ExtraTweaks(game, game.LastScan!));

    /// <summary>Backup destinations that are reachable now (USB plugged in, Drive folder present).</summary>
    public List<string> BackupTargets() => BackupDestinations.Resolve(Settings.Backups.Destinations);

    public ArtworkService Artwork => new(Http, Paths.ArtworkDir, Settings.SteamGridDbApiKey);

    public void SaveLibrary()
    {
        lock (_saveLock) Store.SaveLibrary(Library);
    }

    public void SaveSettings()
    {
        lock (_saveLock) Store.SaveSettings(Settings);
    }

    public void Dispose()
    {
        Temps.Dispose();
        Http.Dispose();
    }
}

public sealed class Log(string path)
{
    private readonly Lock _lock = new();

    public void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2_000_000) File.Delete(path); // keep it small
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
