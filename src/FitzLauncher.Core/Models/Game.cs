using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Models;

/// <summary>A game (or repack) the user has added to the library.</summary>
public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    /// <summary>Root folder of the install. Defaults to the exe's folder, but is widened to the
    /// real game root when the exe lives in e.g. Binaries\Win64.</summary>
    public string InstallDir { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Extra launch options added by optimisations (kept separate so they can be reverted).</summary>
    public string OptimizerArguments { get; set; } = "";
    /// <summary>User-chosen folder holding the game's config files, when auto-detection can't find it.</summary>
    public string? CustomConfigDir { get; set; }
    /// <summary>Local path of the cached cover art (portrait 2:3), if any.</summary>
    public string? ArtworkPath { get; set; }
    /// <summary>Optional wide hero/banner image for the details pane.</summary>
    public string? HeroPath { get; set; }
    public int? SteamAppId { get; set; }

    // ---- Emulation Hub (null/empty for PC games)
    public FitzLauncher.Core.Emulation.EmuPlatform? Platform { get; set; }
    public string? EmulatorId { get; set; }
    /// <summary>The ROM / disc image / game folder the emulator opens.</summary>
    public string? RomPath { get; set; }
    /// <summary>Serial or title ID read from the dump (matching and mods).</summary>
    public string? TitleId { get; set; }
    public List<string> Collections { get; set; } = [];
    public List<FitzLauncher.Core.Emulation.EmuModEntry> EmuMods { get; set; } = [];
    /// <summary>Description, release date, genres and rating from IGDB (emulator games).</summary>
    public GameMeta? Meta { get; set; }
    /// <summary>How this game has run on each emulator (key = emulator id).</summary>
    public Dictionary<string, FitzLauncher.Core.Emulation.EmuPerfRecord> EmuPerf { get; set; } = [];
    public FitzLauncher.Core.Emulation.EmuTuningState EmuTuning { get; set; } = new();
    /// <summary>Move the quality up or down after each session to hold the FPS target.</summary>
    public bool EmuAutoTune { get; set; }
    /// <summary>Tuning preset last applied to this game's emulator settings.</summary>
    public string? TuningPreset { get; set; }

    /// <summary>FPS target chosen for this game (90/100/110/117), or null for "uncapped".</summary>
    public int? FpsTarget { get; set; }
    public bool UseGameMode { get; set; } = true;
    public bool Favourite { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPlayed { get; set; }
    public TimeSpan TotalPlayTime { get; set; }

    public GameScanResult? LastScan { get; set; }
    public DateTimeOffset? OptimizedAt { get; set; }
    /// <summary>Every change the launcher made for this game, newest last, so it can be reverted.</summary>
    public List<AppliedChange> AppliedChanges { get; set; } = [];

    /// <summary>Summary of the most recent session, for the library card badge.</summary>
    public SessionSummary? LastSession { get; set; }

    // ---- organisation & progress
    public List<string> Tags { get; set; } = [];
    public bool Hidden { get; set; }
    public PlayStatus Status { get; set; }
    /// <summary>User-entered length of the main story, used for the progress ring.</summary>
    public double? MainStoryHours { get; set; }
    /// <summary>Manual completion override (0-100). Wins over the hours-based estimate.</summary>
    public int? CompletionPercent { get; set; }

    // ---- performance automation
    public CorePinning CorePinning { get; set; } = CorePinning.Auto;
    public bool ThermalGuard { get; set; } = true;
    /// <summary>Smooth mode: the settings that help the 1% lows are applied for this game (see <c>SmoothMode</c>).</summary>
    public bool SmoothMode { get; set; }
    /// <summary>What each Smooth mode change replaced (empty = the setting was unset), so it can be put back.</summary>
    public Dictionary<string, string> SmoothUndo { get; set; } = [];
    /// <summary>This game's own overlay layout (null: the global one).</summary>
    public OverlaySettings? Overlay { get; set; }
    public AutoTuneState AutoTune { get; set; } = new();

    // ---- display
    /// <summary>Pick resolution/render scale for whichever screen is active at launch (handheld vs monitor).</summary>
    public bool AutoDisplay { get; set; } = true;
    /// <summary>Learned render scale and FPS per display ("3440x1440@144").</summary>
    public Dictionary<string, DisplayProfile> DisplayProfiles { get; set; } = [];

    // ---- power (APU TDP via RyzenAdj)
    public bool PowerAuto { get; set; } = true;
    /// <summary>Learned sustained power limit for this game, in watts (null = use the default).</summary>
    public int? PowerWatts { get; set; }
    /// <summary>Lowest power limit that held each FPS target (learned live in sessions).</summary>
    public Dictionary<int, int> PowerByTarget { get; set; } = [];
    /// <summary>This game's own auto TDP choice (null: the global setting).</summary>
    public FitzLauncher.Core.Optimization.AutoTdpMode? AutoTdp { get; set; }
    /// <summary>What auto TDP learned per FPS target: the power range that held it.</summary>
    public Dictionary<int, FitzLauncher.Core.Optimization.AutoTdpProfile> AutoTdpProfiles { get; set; } = [];

    // ---- RTSS limiter mode A/B (0 = async, 1 = front-edge sync)
    public Dictionary<int, LimiterResult> LimiterResults { get; set; } = [];
    public int? LimiterChosen { get; set; }

    // ---- extras
    public AchievementSnapshot? Achievements { get; set; }

    // ---- stability
    /// <summary>End of the last session that ran without a crash; changes after it are rollback candidates.</summary>
    public DateTimeOffset? LastGoodSession { get; set; }
    public CrashInfo? LastCrash { get; set; }
    public int CrashCount { get; set; }

    /// <summary>Pause this game while the device sleeps (turn off for online games that drop the connection).</summary>
    public bool PauseOnSleep { get; set; } = true;

    // ---- mods & trainer
    public ModSetup ModSetup { get; set; } = new();
    public TrainerConfig? Trainer { get; set; }

    /// <summary>Per-game controller tuning (deadzones, curves, vibration); null = use the default profile.</summary>
    public ControllerProfile? Controller { get; set; }

    /// <summary>Official-version update check and the updates applied through the launcher.</summary>
    public GameUpdateInfo Updates { get; set; } = new();

    /// <summary>Remember a separate FPS cap for each PC and screen this game is played on.</summary>
    public bool CapPerDevice { get; set; }
    public Dictionary<string, FitzLauncher.Core.Optimization.DeviceCap> DeviceCaps { get; set; } = [];
    /// <summary>Cooling mode for this game; null = the default in Settings.</summary>
    public FitzLauncher.Core.Optimization.CoolingMode? Cooling { get; set; }

    /// <summary>A/B benchmark in progress or finished (null = none).</summary>
    public FitzLauncher.Core.Optimization.AbTest? AbTest { get; set; }

    /// <summary>AMD Software features for this game (Anti-Lag, Chill, RSR, AFMF, sharpening, Boost).</summary>
    public AmdProfile Amd { get; set; } = new();

    /// <summary>Last computed report-card grade (A–F), shown on the library card.</summary>
    public string? ReportGrade { get; set; }

    // ---- save backups
    public List<string> SaveDirs { get; set; } = [];
    public bool BackupSaves { get; set; } = true;
    public DateTimeOffset? LastSaveBackup { get; set; }
    public string? LastSaveFingerprint { get; set; }

    [JsonIgnore]
    public string ExeName => Path.GetFileName(ExePath);

    /// <summary>0-100 main-story progress (see <see cref="Stats.MainStoryProgress"/>); achievements do not count.</summary>
    [JsonIgnore]
    public double? ProgressPercent => Stats.MainStoryProgress.For(this)?.Percent;
}

public sealed class CrashInfo
{
    public DateTimeOffset When { get; set; }
    public string? FaultingModule { get; set; }
    public int? ExitCode { get; set; }
    public bool UndervoltActive { get; set; }
}

public sealed class LimiterResult
{
    /// <summary>1% low ÷ average: closer to 1 = more even frame pacing.</summary>
    public double Smoothness { get; set; }
    public int Sessions { get; set; }
}

public sealed class AchievementSnapshot
{
    public int Earned { get; set; }
    public int? Total { get; set; }
    public DateTimeOffset Read { get; set; } = DateTimeOffset.Now;
}

public sealed class DisplayProfile
{
    public string Label { get; set; } = "";
    /// <summary>Render scale percent (100 = native). Only applied on engines that support it.</summary>
    public int RenderScale { get; set; } = 100;
    public double? LastAvgFps { get; set; }
    public double? LastLowFps { get; set; }
    public int Sessions { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
}

public enum PlayStatus { None, Backlog, Playing, Beaten, Completed, Abandoned }

/// <summary>Which CPU cores the game may run on.</summary>
public enum CorePinning
{
    /// <summary>Pin only where it reliably helps: V-Cache CCD on dual-CCD X3D, P-cores on Intel hybrid.</summary>
    Auto,
    Off,
    PerformanceCores,
    VCacheCcd,
    /// <summary>First CCD only, on CPUs with two equal CCDs (Ryzen 9 / Ryzen AI Max). Test it with a benchmark.</summary>
    SingleCcd,
}

public sealed class AutoTuneState
{
    public bool Enabled { get; set; } = true;
    public bool TargetReached { get; set; }
    public List<AutoTuneStep> Steps { get; set; } = [];
}

public enum AutoTuneOutcome { Pending, Kept, Reverted, Skipped }

/// <summary>One experiment: a suggestion applied after a session that missed the target, judged by the next session.</summary>
public sealed class AutoTuneStep
{
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.Now;
    public int? TargetFps { get; set; }
    public double BaselineAvg { get; set; }
    public double BaselineLow { get; set; }
    public double BaselineAtTarget { get; set; }
    public double? ResultAvg { get; set; }
    public double? ResultLow { get; set; }
    public double? ResultAtTarget { get; set; }
    public AutoTuneOutcome Outcome { get; set; }
}

public sealed class GameMeta
{
    public string? Summary { get; set; }
    public string? Released { get; set; }
    public List<string> Genres { get; set; } = [];
    public string? Developer { get; set; }
    public double? Rating { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}
