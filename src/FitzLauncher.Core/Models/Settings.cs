namespace FitzLauncher.Core.Models;

public sealed class LauncherSettings
{
    public static readonly int[] FpsTargets = [90, 100, 110, 117, 141];

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
    /// <summary>Overlay layout and items for games without their own (null: built from the old style setting).</summary>
    public OverlaySettings? OverlayLayout { get; set; }
    public OverlaySettings EffectiveOverlay => OverlayLayout ??= OverlaySettings.FromLegacy(RtssOverlay, Overlay);
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
    /// <summary>Install a new version found at start-up without asking (skipped while a game runs).</summary>
    public bool AutoInstallUpdates { get; set; } = true;
    /// <summary>DPAPI-protected GitHub token, only needed while the repository is private.</summary>
    public string? GitHubTokenProtected { get; set; }

    // ---- mods
    /// <summary>Nexus Mods personal API key, encrypted with Windows DPAPI for this user.</summary>
    public string? NexusApiKeyProtected { get; set; }

    /// <summary>Emulation Hub settings (folders, update checks, metadata provider keys).</summary>
    public EmulationSettings Emulation { get; set; } = new();
    public string? ModIoApiKeyProtected { get; set; }
    /// <summary>Open Nexus "Mod Manager Download" (nxm://) links in the launcher.</summary>
    public bool NxmHandler { get; set; }

    /// <summary>Accent and background colours follow the selected game's cover art.</summary>
    public bool MatchGameColours { get; set; } = true;

    // ---- controllers
    /// <summary>Drive the launcher's menus with a controller (D-pad/stick, A select, B back, LB/RB tabs).</summary>
    public bool GamepadNavigation { get; set; } = true;
    /// <summary>Controller tuning used for games without their own profile (off until enabled).</summary>
    public ControllerProfile DefaultController { get; set; } = new();
    /// <summary>The PS button on a DualShock/DualSense opens OneXConsole (press again to go back), in games too.</summary>
    public bool PsButtonOpensConsole { get; set; }
    /// <summary>OneXConsole's exe or shortcut when it isn't found automatically.</summary>
    public string? ConsoleAppPath { get; set; }
    /// <summary>While OneXConsole is in front, mirror the Sony pad as an Xbox controller so it can drive it.</summary>
    public bool ConsolePadMirror { get; set; } = true;
    /// <summary>Hold PS + a button for Windows actions (volume, brightness, screenshot…).</summary>
    public FitzLauncher.Core.Input.PadComboSettings PadCombos { get; set; } = FitzLauncher.Core.Input.PadComboSettings.New();
    /// <summary>Old setting (counters stayed hidden for later games); no longer used: PS + Right now lasts for one game.</summary>
    public bool OverlayHiddenByPad { get; set; }

    // ---- mods
    /// <summary>Check (and auto-repair) the mod setup before each launch.</summary>
    public bool ModLaunchCheck { get; set; } = true;
    /// <summary>Track mods on your Nexus account when Fitz installs them (update emails, "Tracked" list on Nexus).</summary>
    public bool NexusTrackOnInstall { get; set; } = true;

    // ---- game help (AI Game Companion)
    /// <summary>Open AI Game Companion with every game.</summary>
    public bool GameHelpWithGames { get; set; }
    /// <summary>Its launcher.exe when not found automatically.</summary>
    public string? GameHelpPath { get; set; }

    // ---- game updates
    /// <summary>Check official sources for game updates on the first launch of each day.</summary>
    public bool GameUpdateCheckDaily { get; set; } = true;
    public DateTimeOffset? LastGameUpdateCheck { get; set; }

    /// <summary>Launcher version the device check last ran for (it runs again after each update).</summary>
    public string? DeviceCheckedVersion { get; set; }

    // ---- upscalers
    /// <summary>Download new official XeSS/FSR(/DLSS) DLLs daily and swap them into games.</summary>
    public bool KeepUpscalersUpdated { get; set; } = true;
    public Dictionary<string, string> UpscalerEtags { get; set; } = [];
    public DateTimeOffset? LastUpscalerCheck { get; set; }

    /// <summary>Open straight into Big Picture (handheld).</summary>
    public bool StartInBigPicture { get; set; }
    /// <summary>The launcher window fills the screen with no title bar (Alt+Enter toggles).</summary>
    public bool FullScreen { get; set; }

    /// <summary>Cooling mode for games without their own.</summary>
    public FitzLauncher.Core.Optimization.CoolingMode DefaultCooling { get; set; } = FitzLauncher.Core.Optimization.CoolingMode.Off;
    /// <summary>The nxm:// handler that was registered before ours (Vortex/MO2), restored when turned off.</summary>
    public string? NxmPreviousCommand { get; set; }

    // ---- power limits (APUs) through RyzenAdj
    public bool PowerControl { get; set; }
    public string? RyzenAdjPath { get; set; }
    public int PowerMinWatts { get; set; } = 25;
    public int PowerMaxWatts { get; set; } = 85;
    /// <summary>Adjust the APU power limit live in each session to the lowest that holds the target.</summary>
    public bool LivePowerTuning { get; set; } = true;
    /// <summary>Auto TDP for handhelds: Off, Observe (logs what it would do) or On (holds the FPS target with the least power). Off by default.</summary>
    public FitzLauncher.Core.Optimization.AutoTdpMode AutoTdp { get; set; }
    /// <summary>Where Auto TDP starts for a game it hasn't learned yet (a known-good middle: it then goes up or down from here).</summary>
    public int AutoTdpStartWatts { get; set; } = 45;
    /// <summary>What Auto TDP last did, in plain English (shown in Settings so "why isn't it working?" has an answer).</summary>
    public string? AutoTdpLastNote { get; set; }
    public int AutoTdpMinWatts { get; set; } = 25;
    public int AutoTdpMaxWatts { get; set; } = 60;
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
    /// <summary>Resource guard: frees GPU, CPU and memory for the game at launch and while playing (part of Game Mode).</summary>
    public FitzLauncher.Core.Optimization.ResourceGuardOptions ResourceGuard { get; set; } = new();
    /// <summary>Lower the cap when there's no input for a while (menus, cutscenes, AFK), like Radeon Chill.</summary>
    public bool AdaptiveCap { get; set; } = true;
    public int AdaptiveIdleSeconds { get; set; } = 20;
    public int AdaptiveIdleCap { get; set; } = 60;
    /// <summary>Hold View + LB in-game for the quick HUD (D-pad: target up/down, power left/right).</summary>
    public bool QuickMenu { get; set; } = true;
    /// <summary>The older XInput shortcuts (View + D-pad up/down for the FPS target, View + LB for the quick menu).
    /// Off by default: PS + a button, and the PS actions wheel, are the controller shortcuts.</summary>
    public bool LegacyPadShortcuts { get; set; }
    /// <summary>Freeze the game when the screen turns off or the PC sleeps; unfreeze on wake once signed in.</summary>
    public bool PauseOnSleep { get; set; } = true;
    /// <summary>Let Windows' idle timers turn off the screen / sleep during a game; controller input counts as activity.</summary>
    public bool IdleSleepInGame { get; set; } = true;
    /// <summary>While asleep: best power efficiency mode, a low APU power limit and no launcher polling (quiet fans).</summary>
    public bool LowPowerSleep { get; set; } = true;
    /// <summary>APU power limit while asleep (RyzenAdj).</summary>
    public int SleepWatts { get; set; } = 6;
    /// <summary>Windows "hibernate after" while asleep, in minutes (0 = never). Applied when changed.</summary>
    public int HibernateAfterMinutes { get; set; }
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
    /// <summary>The guard only acts once the hotter of CPU/GPU reaches this (°C). Cooler modes lower it.</summary>
    public double TriggerC { get; set; } = 90;
    /// <summary>It steps back up once this many degrees below the trigger…</summary>
    public double ReleaseBelowC { get; set; } = 5;
    /// <summary>…for this long.</summary>
    public int RecoverAfterSeconds { get; set; } = 45;
    /// <summary>Seconds at or above the trigger before each step down.</summary>
    public int StepDownAfterSeconds { get; set; } = 15;
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

public sealed class EmulationSettings
{
    /// <summary>Where downloaded emulators live (versions\, data\, mods-library\). Empty = the launcher's data folder.</summary>
    public string EmuRoot { get; set; } = "";
    public List<string> ScanFolders { get; set; } = [];
    /// <summary>Emulator id → the program the user picked or Fitz found.</summary>
    public Dictionary<string, string> EmulatorPaths { get; set; } = [];
    /// <summary>Platform name (EmuPlatform) → folders holding that console's games.</summary>
    public Dictionary<string, List<string>> RomFolders { get; set; } = [];
    public int CheckIntervalHours { get; set; } = 6;
    /// <summary>"stable" or "nightly" per emulator id; missing = stable.</summary>
    public Dictionary<string, string> Channels { get; set; } = [];
    // Keys are DPAPI-encrypted like the Nexus key.
    public string? SteamGridDbKeyProtected { get; set; }
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecretProtected { get; set; }
    public string? ScreenScraperUser { get; set; }
    public string? ScreenScraperPasswordProtected { get; set; }
}
