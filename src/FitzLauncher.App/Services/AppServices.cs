using System.Net.Http;
using FitzLauncher.Core.Artwork;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;
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
    }

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
