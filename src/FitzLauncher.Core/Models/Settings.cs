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

    public ThermalGuardOptions ThermalGuard { get; set; } = new();
    public BackupOptions Backups { get; set; } = new();

    /// <summary>Old on/off switch, kept so existing settings files still load. Use <see cref="Overlay"/>.</summary>
    public bool RtssOverlay { get; set; } = true;
    /// <summary>The launcher's line in the RTSS on-screen display (its own slot; Afterburner's lines are untouched).</summary>
    public OverlayStyle Overlay { get; set; } = OverlayStyle.FpsAndTemps;
    /// <summary>Use MSI Afterburner's sensor readings when it's running, so two tools don't poll the same sensors.</summary>
    public bool PreferAfterburnerSensors { get; set; } = true;
    public bool AutoTuneNewGames { get; set; } = true;
    public int BenchmarkSeconds { get; set; } = 60;
    /// <summary>Folder with newer DLSS/XeSS/FSR DLLs to offer as upgrades (in addition to other games' copies).</summary>
    public string? UpscalerDllFolder { get; set; }

    /// <summary>Choose resolution/render scale per active display at launch.</summary>
    public bool AutoDisplay { get; set; } = true;
    /// <summary>For engines without a render-scale setting, allow a lower output resolution to reach the target.</summary>
    public bool AllowLowerOutputResolution { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>DPAPI-protected GitHub token, only needed while the repository is private.</summary>
    public string? GitHubTokenProtected { get; set; }

    // ---- power limits (APUs) through RyzenAdj
    public bool PowerControl { get; set; }
    public string? RyzenAdjPath { get; set; }
    public int PowerMinWatts { get; set; } = 25;
    public int PowerMaxWatts { get; set; } = 85;
    /// <summary>Starting point for a game with nothing learned yet.</summary>
    public int PowerDefaultWatts { get; set; } = 65;

    /// <summary>Hold View/Back + D-pad up/down (or Ctrl+Alt+Up/Down) to change the FPS target in-game.</summary>
    public bool QuickSwitch { get; set; } = true;
    /// <summary>Let auto-tune compare RTSS's async and front-edge-sync limiters per game.</summary>
    public bool AutoLimiterMode { get; set; } = true;

    // ---- batch 4
    /// <summary>Keep every CPU core awake while playing (skipped on X3D, which relies on parking).</summary>
    public bool CoreParkingOff { get; set; } = true;
    /// <summary>Throttle background apps that suddenly hog the CPU mid-game.</summary>
    public bool BackgroundLimiter { get; set; } = true;
    /// <summary>Lower the cap when there's no input for a while (menus, cutscenes, AFK), like Radeon Chill.</summary>
    public bool AdaptiveCap { get; set; } = true;
    public int AdaptiveIdleSeconds { get; set; } = 12;
    public int AdaptiveIdleCap { get; set; } = 60;
    /// <summary>Hold View + LB in-game for the quick HUD (D-pad: target up/down, power left/right).</summary>
    public bool QuickMenu { get; set; } = true;
    /// <summary>Ryzen all-core Curve Optimizer offset via RyzenAdj, approached in small steps.</summary>
    public bool UndervoltEnabled { get; set; }
    public int UndervoltTarget { get; set; } = -15;
    /// <summary>Offset reached so far (0 = stock). Steps toward the target only after clean sessions.</summary>
    public int UndervoltCurrent { get; set; }
    public bool UndervoltSettled { get; set; }

    public bool SyncEnabled { get; set; }
    public string? SyncFolder { get; set; }

    /// <summary>Folders the user scans for games ("Scan game folders"); rechecked for new games at start-up.</summary>
    public List<string> LibraryFolders { get; set; } = [];

    public bool StartWithWindows { get; set; }
    public bool NoUacShortcut { get; set; }
    /// <summary>GPU driver version seen last run; a change prompts a shader-cache clean.</summary>
    public string? LastSeenDriverVersion { get; set; }
}

public enum OverlayStyle { Off, FpsAndTemps, Detailed }

public sealed class ThermalGuardOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Step the cap down when within this many degrees of the alert limit.</summary>
    public double MarginC { get; set; } = 2;
    public int StepDownAfterSeconds { get; set; } = 15;
    /// <summary>Step back up once this far below the limit…</summary>
    public double RecoverMarginC { get; set; } = 8;
    /// <summary>…for this long.</summary>
    public int RecoverAfterSeconds { get; set; } = 90;
    public int MinimumCap { get; set; } = 60;
}

public enum BackupDestinationKind { Folder, GoogleDrive, Usb }

public sealed class BackupDestination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BackupDestinationKind Kind { get; set; }
    /// <summary>Folder path (for USB: the path on the drive when it was added; the drive is found by its marker file).</summary>
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>USB only: id written to the drive's marker file.</summary>
    public Guid? DriveId { get; set; }
}

public sealed class BackupOptions
{
    public bool Enabled { get; set; } = true;
    public int KeepPerGame { get; set; } = 10;
    /// <summary>Extra copies beyond the local backup folder (Google Drive for desktop folder, USB drive, NAS…).</summary>
    public List<BackupDestination> Destinations { get; set; } = [];
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
