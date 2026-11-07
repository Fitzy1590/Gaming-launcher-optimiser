namespace FitzLauncher.Core.Models;

public sealed class LauncherSettings
{
    public static readonly int[] FpsTargets = [90, 100, 110, 117];

    public int DefaultFpsTarget { get; set; } = 117;

    public GameModeOptions GameMode { get; set; } = new();

    public double CpuTempAlertC { get; set; } = 90;
    public double GpuTempAlertC { get; set; } = 85;
    /// <summary>The temperature must stay above the threshold this long before an alert fires.</summary>
    public int TempAlertSustainSeconds { get; set; } = 10;
    public bool TempAlertSound { get; set; } = true;
    public bool TempAlertOverlay { get; set; } = true;

    public bool TrackFps { get; set; } = true;
    public bool MinimizeWhilePlaying { get; set; } = true;
    /// <summary>Games normally start un-elevated (like a double-click). Some old games need admin.</summary>
    public bool LaunchGamesAsAdmin { get; set; }

    /// <summary>Optional SteamGridDB API key for higher-quality artwork. Steam's store art is used without it.</summary>
    public string? SteamGridDbApiKey { get; set; }
}

public sealed class GameModeOptions
{
    public bool Enabled { get; set; } = true;
    public bool SwitchPowerPlan { get; set; } = true;
    public bool ClearRam { get; set; } = true;
    /// <summary>Keep purging the standby list during play when free memory runs low (like ISLC).</summary>
    public bool StandbyListGuard { get; set; } = true;
    public bool StopServices { get; set; } = true;
    public bool CloseBackgroundApps { get; set; } = true;
    public bool RaiseGamePriority { get; set; } = true;
    public bool LowerBackgroundPriority { get; set; } = true;
    public bool HighResolutionTimer { get; set; } = true;

    /// <summary>Services stopped while playing and restarted afterwards (only ones that were running).</summary>
    public List<string> Services { get; set; } =
    [
        "wuauserv",   // Windows Update
        "UsoSvc",     // Update Orchestrator
        "BITS",       // Background Intelligent Transfer (background downloads)
        "DoSvc",      // Delivery Optimization (peer-to-peer update uploads/downloads)
        "WSearch",    // Search indexer (disk + CPU spikes)
        "DiagTrack",  // Telemetry uploads
        "MapsBroker", // Offline maps downloads
        "edgeupdate", "edgeupdatem",
        "gupdate", "gupdatem",
    ];

    /// <summary>Processes that are suspended during play and resumed afterwards (sync clients).</summary>
    public List<string> SuspendProcesses { get; set; } = ["OneDrive.exe"];

    /// <summary>Processes that are closed during play (updaters; they restart on their own).</summary>
    public List<string> CloseProcesses { get; set; } =
    [
        "MicrosoftEdgeUpdate.exe", "GoogleUpdate.exe", "AdobeARM.exe", "AdobeUpdateService.exe",
        "SoftwareUpdate.exe", "jusched.exe", "CCleanerUpdate.exe",
    ];

    /// <summary>Processes dropped to below-normal priority while playing.</summary>
    public List<string> LowPriorityProcesses { get; set; } =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe",
        "Spotify.exe", "Teams.exe", "ms-teams.exe", "SearchHost.exe", "OneDrive.Sync.Service.exe",
        "steamwebhelper.exe", "EpicWebHelper.exe",
    ];
}
