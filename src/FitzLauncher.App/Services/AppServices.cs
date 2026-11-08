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
using FitzLauncher.App.Services.Mods;

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
    public UpdateService Updates { get; }

    /// <summary>RyzenAdj, when power control is on and the tool was found.</summary>
    public RyzenAdjClient? RyzenAdj =>
        Settings.PowerControl && RyzenAdjClient.Find(Settings.RyzenAdjPath) is { } path ? new RyzenAdjClient(path, Paths) : null;

    /// <summary>The sync folder: the user's choice, else Google Drive's "My Drive".</summary>
    public LibrarySync? Sync =>
        Settings.SyncEnabled && (Settings.SyncFolder ?? BackupDestinations.FindGoogleDrive()) is { } root && Directory.Exists(root)
            ? new LibrarySync(Paths, root) : null;

    /// <summary>Copies library/settings/history to the sync folder (quietly; errors only logged).</summary>
    public void SyncUpload()
    {
        try { Sync?.Upload(Environment.MachineName, UpdateService.CurrentVersion.ToString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write("Sync upload failed: " + e.Message); }
    }

    /// <summary>Reads achievement progress from the repack's emulator files (total from the repack or Steam).</summary>
    public async Task<FitzLauncher.Core.Progress.AchievementProgress?> RefreshAchievementsAsync(Game game)
    {
        var total = game.Achievements?.Total;
        var appId = game.LastScan?.SteamAppId ?? game.SteamAppId;
        if (total is null && appId is { } id) total = await SteamAchievementTotalAsync(id);
        var p = await Task.Run(() => FitzLauncher.Core.Progress.AchievementReader.Read(game, game.LastScan, SaveContext, total));
        if (p is null) return null;
        game.Achievements = new AchievementSnapshot { Earned = p.Earned, Total = p.Total };
        SaveLibrary();
        return p;
    }

    /// <summary>Total achievements from Steam's public store data (used when the repack doesn't list them).</summary>
    public async Task<int?> SteamAchievementTotalAsync(int appId)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=achievements");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(appId.ToString(), out var app) && app.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("achievements", out var a) && a.TryGetProperty("total", out var t) ? t.GetInt32() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
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
        Updates = new UpdateService(new HttpClient { Timeout = TimeSpan.FromMinutes(10), DefaultRequestHeaders = { { "User-Agent", "FitzLauncher-updater" } } }, this);
        Temps.PreferAfterburner = () => Settings.PreferAfterburnerSensors;
    }

    /// <summary>Per-game presets plus DLL upgrades, merged into each game's plan as suggestions.</summary>
    public List<Tweak> ExtraTweaks(Game game, GameScanResult scan)
    {
        var list = PresetCatalog.For(game, scan, PresetCatalog.Load(Paths.Presets)).ToList();
        list.AddRange(UpscalerLibrary.Upgrades(scan, BestUpscalerDlls()));
        if (DxvkPlanner.Plan(game, scan, Dxvk.Root, Hardware) is { } dxvk) list.Add(dxvk);
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

    // ---------------------------------------------------------------- mods & trainers
    private ModService? _mods;
    private TrainerService? _trainers;
    private ThunderstoreClient? _thunderstore;
    public ModService Mods => _mods ??= new ModService(this);
    private GameUpdateService? _gameUpdates;
    public GameUpdateService GameUpdates => _gameUpdates ??= new GameUpdateService(this);
    public TrainerService Trainers => _trainers ??= new TrainerService(this);
    /// <summary>Longer timeout and a User-Agent for mod stores and downloads.</summary>
    public HttpClient StoreHttp { get; } = CreateStoreHttp();
    public ThunderstoreClient Thunderstore => _thunderstore ??= new ThunderstoreClient(StoreHttp, Paths.Root);
    public string? NexusKey => Secrets.Unprotect(Settings.NexusApiKeyProtected);
    public NexusClient? Nexus => NexusKey is { } k ? new NexusClient(StoreHttp, k, Paths.Root) : null;
    public ModIoClient? ModIo => Secrets.Unprotect(Settings.ModIoApiKeyProtected) is { } k ? new ModIoClient(StoreHttp, k) : null;

    private static HttpClient CreateStoreHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("FitzLauncher/" + UpdateService.CurrentVersion);
        return h;
    }

    public DxvkService Dxvk => new(Path.Combine(Paths.Root, "dxvk"));

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
