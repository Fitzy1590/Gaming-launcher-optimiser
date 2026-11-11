namespace FitzLauncher.Core.Optimization;

// Resource guard: frees GPU, CPU and memory for the game. Pure decisions here; the Windows project
// measures the processes and applies the result. Per-process GPU figures come from the same Windows
// counters Task Manager uses (the approach shanselman/gputaskman takes on Windows).

/// <summary>What can be done to a background app. Restrain and TrimMemory lose nothing and are undone afterwards.</summary>
public enum FreeAction
{
    /// <summary>Lowest CPU priority, Windows efficiency mode and lowest GPU scheduling priority, until the game ends.</summary>
    Restrain,
    /// <summary>Moves the app's idle memory out of RAM; it comes back as the app needs it.</summary>
    TrimMemory,
    /// <summary>Freezes the app until the game ends (it keeps its state, but does nothing meanwhile).</summary>
    Pause,
    /// <summary>Asks the app to close, the same as clicking its X. Never forced.</summary>
    Close,
}

public enum ResourceChoice { Ask, Always, Never }

public enum AppClass
{
    /// <summary>Windows itself and security software: never touched.</summary>
    System,
    /// <summary>Things the game or the device needs (launchers, anti-cheat, overlays, handheld and GPU tools): never touched.</summary>
    Companion,
    /// <summary>Apps you may be using while you play (voice chat, recording, music): only ever suggested.</summary>
    UserActive,
    Background,
}

public enum OutcomeVerdict { Helped, NoChange, Hurt }

/// <summary>One process as measured. CPU and GPU are percentages of the whole machine.</summary>
public sealed record ProcessUsage(int Pid, string Name, double CpuPercent, long RamMb, double GpuPercent, long VramMb, bool HasWindow = false);

/// <summary>All processes of one app (same name) added up.</summary>
public sealed record AppUsage(string Name, IReadOnlyList<int> Pids, double CpuPercent, long RamMb, double GpuPercent, long VramMb, bool HasWindow)
{
    /// <summary>Rough size of what the app takes from the game, for ordering.</summary>
    public double Impact => GpuPercent * 3 + CpuPercent * 2 + VramMb / 100.0 + RamMb / 200.0;
}

public sealed record MachineState(long TotalRamMb, long FreeRamMb);

public sealed class ResourceGuardOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Show the optional suggestions before a game starts.</summary>
    public bool AskAtLaunch { get; set; } = true;
    /// <summary>Keep watching while the game runs.</summary>
    public bool LiveGuard { get; set; } = true;
}

// ---------------------------------------------------------------- what was chosen and what worked

public sealed class ResourceRule
{
    public string Name { get; set; } = "";
    public ResourceChoice Choice { get; set; }
    /// <summary>The action to repeat when <see cref="Choice"/> is Always.</summary>
    public FreeAction? Action { get; set; }
    public DateTimeOffset Updated { get; set; }
}

/// <summary>A finished session of one game with the launch steps taken ("name|action"), for comparing the next one against.</summary>
public sealed class LaunchRun
{
    public List<string> Actions { get; set; } = [];
    public double Fps { get; set; }
    public double LowFps { get; set; }
    public double? TempC { get; set; }
    public double StuttersPerMinute { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class ResourceOutcome
{
    public string Name { get; set; } = "";
    public FreeAction Action { get; set; }
    public int Helped { get; set; }
    public int NoChange { get; set; }
    public int Hurt { get; set; }
    /// <summary>Running averages over every try, in percent (FPS, 1% low) and °C.</summary>
    public double FpsGainPercent { get; set; }
    public double LowGainPercent { get; set; }
    public double TempDropC { get; set; }
    public DateTimeOffset Last { get; set; }
    public string? LastGame { get; set; }
    public OutcomeVerdict LastVerdict { get; set; }
    public int Tries => Helped + NoChange + Hurt;
}

/// <summary>An app that was busy while the game was struggling.</summary>
public sealed class ResourceOffender
{
    public string Name { get; set; } = "";
    /// <summary>"GPU", "CPU" or "memory".</summary>
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset Last { get; set; }
}

/// <summary>Saved as <c>resource-guard.json</c>: your choices and a history of what helped and what didn't.</summary>
public sealed class ResourceMemory
{
    public List<ResourceRule> Rules { get; set; } = [];
    public List<ResourceOutcome> Outcomes { get; set; } = [];
    public List<ResourceOffender> Offenders { get; set; } = [];
    /// <summary>Per game id: how the last long session went and which steps were taken at its launch.</summary>
    public Dictionary<string, LaunchRun> LastRuns { get; set; } = [];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public ResourceRule? RuleFor(string name) => Rules.FirstOrDefault(r => Same(r.Name, name));

    public void Remember(string name, ResourceChoice choice, FreeAction? action, DateTimeOffset now)
    {
        Rules.RemoveAll(r => Same(r.Name, name));
        if (choice != ResourceChoice.Ask) Rules.Add(new ResourceRule { Name = name, Choice = choice, Action = choice == ResourceChoice.Always ? action : null, Updated = now });
    }

    public ResourceOutcome? OutcomeFor(string name, FreeAction action) => Outcomes.FirstOrDefault(o => Same(o.Name, name) && o.Action == action);

    /// <summary>Twice worse and never better: this action is no longer taken on its own for this app.</summary>
    public bool Blocked(string name, FreeAction action) => OutcomeFor(name, action) is { Hurt: >= 2, Helped: 0 };

    public ResourceOutcome Record(string name, FreeAction action, OutcomeVerdict verdict, PerfWindow before, PerfWindow after, string? game, DateTimeOffset now)
    {
        var o = OutcomeFor(name, action);
        if (o is null) Outcomes.Add(o = new ResourceOutcome { Name = name, Action = action });
        var n = o.Tries;
        double Avg(double old, double x) => (old * n + x) / (n + 1);
        if (before.Fps > 0) o.FpsGainPercent = Avg(o.FpsGainPercent, (after.Fps - before.Fps) / before.Fps * 100);
        if (before.LowFps > 0) o.LowGainPercent = Avg(o.LowGainPercent, (after.LowFps - before.LowFps) / before.LowFps * 100);
        if (before.TempC is { } bt && after.TempC is { } at) o.TempDropC = Avg(o.TempDropC, bt - at);
        switch (verdict)
        {
            case OutcomeVerdict.Helped: o.Helped++; break;
            case OutcomeVerdict.Hurt: o.Hurt++; break;
            default: o.NoChange++; break;
        }
        o.Last = now;
        o.LastGame = game;
        o.LastVerdict = verdict;
        return o;
    }

    public static string ActionKey(string name, FreeAction action) => name.ToLowerInvariant() + "|" + action;

    /// <summary>Longest gap between two sessions that still count as the same situation.</summary>
    public static readonly TimeSpan CompareWithin = TimeSpan.FromDays(14);

    /// <summary>
    /// Compares this session with the last one of the same game. Only when the two differ by exactly one launch step is
    /// anything concluded, and only for a clear difference (twice the live thresholds): different scenes play differently.
    /// Either way this session becomes the one to compare the next against.
    /// </summary>
    public (string Name, FreeAction Action, OutcomeVerdict Verdict)? JudgeLaunch(string gameId, IReadOnlyList<(string Name, FreeAction Action)> actions, PerfWindow run, DateTimeOffset now)
    {
        var keys = actions.Select(a => ActionKey(a.Name, a.Action)).Distinct().Order().ToList();
        (string, FreeAction, OutcomeVerdict)? found = null;
        if (LastRuns.TryGetValue(gameId, out var last) && now - last.At <= CompareWithin)
        {
            var prev = last.Actions.ToHashSet();
            var added = keys.Where(k => !prev.Contains(k)).ToList();
            var removed = prev.Where(k => !keys.Contains(k)).ToList();
            if (added.Count + removed.Count == 1)
            {
                var before = new PerfWindow(last.Fps, last.LowFps, last.TempC, last.StuttersPerMinute);
                var verdict = ResourceJudge.Judge(before, run, 2);
                var key = added.Count == 1 ? added[0] : removed[0];
                var pair = actions.Concat(removed.Select(_ => (Name: "", Action: FreeAction.Pause))).FirstOrDefault(a => ActionKey(a.Name, a.Action) == key);
                string name; FreeAction action;
                if (added.Count == 1) (name, action) = pair;
                else
                {
                    var parts = key.Split('|');
                    name = parts[0];
                    action = Enum.Parse<FreeAction>(parts[1]);
                    // The step was dropped: a better session without it means it was costing something.
                    verdict = verdict switch { OutcomeVerdict.Helped => OutcomeVerdict.Hurt, OutcomeVerdict.Hurt => OutcomeVerdict.Helped, _ => verdict };
                }
                if (name.Length > 0)
                {
                    if (added.Count == 1) Record(name, action, verdict, before, run, null, now);
                    else Record(name, action, verdict, run, before, null, now);
                    found = (name, action, verdict);
                }
            }
        }
        LastRuns[gameId] = new LaunchRun { Actions = keys, Fps = run.Fps, LowFps = run.LowFps, TempC = run.TempC, StuttersPerMinute = run.StuttersPerMinute, At = now };
        return found;
    }

    public void NoteOffender(string name, string kind, DateTimeOffset now)
    {
        var o = Offenders.FirstOrDefault(x => Same(x.Name, name) && x.Kind == kind);
        if (o is null) Offenders.Add(o = new ResourceOffender { Name = name, Kind = kind });
        o.Count++;
        o.Last = now;
    }

    public ResourceOffender? OffenderFor(string name) => Offenders.Where(o => Same(o.Name, name)).MaxBy(o => o.Count);
}

// ---------------------------------------------------------------- which apps may be touched

public static class AppClassifier
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso", "svchost", "dwm", "explorer",
        "audiodg", "fontdrvhost", "ctfmon", "sihost", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "conhost", "dllhost", "spoolsv", "WUDFHost", "Memory Compression", "MemCompression", "vmmem",
        "WmiPrvSE", "SystemSettings", "LockApp", "Taskmgr", "ApplicationFrameHost", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "smartscreen", "WerFault", "consent", "LogonUI", "UserOOBEBroker",
    };

    private static readonly HashSet<string> CompanionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FitzLauncher", "RTSS", "RTSSHooksLoader64", "MSIAfterburner", "EncoderServer", "ryzenadj", "HidHideCLI", "LibreHardwareMonitor",
        "HWiNFO64", "HWiNFO32", "WeMod", "GameOverlayUI", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Agent", "EADesktop",
        "EABackgroundService", "EALocalHostSvc", "Origin", "upc", "UbisoftConnect", "UplayWebCore", "UbisoftGameLauncher", "RockstarService",
        "SocialClubHelper", "BEService", "vgc", "vgtray", "FACEITService", "faceit", "HandheldCompanion", "cncmd", "atieclxx", "atiesrxx",
        "nvsphelper64", "igfxEM", "sunshine", "parsecd", "Moonlight", "Playnite.DesktopApp", "Playnite.FullscreenApp", "ReShade",
        "SpecialK", "SKIF", "lsfg", "LosslessScaling", "Magpie",
    };

    // Families with many helper processes.
    private static readonly string[] CompanionPrefixes =
    [
        "steam", "Galaxy", "GOG Galaxy", "Xbox", "GameBar", "GamingServices", "EasyAntiCheat", "OneX", "ArmouryCrate", "ArmourySocketServer",
        "ROG", "Legion", "AMDRS", "Radeon", "NVIDIA", "nvcontainer", "NVDisplay", "MSI Center", "MSI_Center", "ViGEm", "AyaSpace", "GPD",
    ];

    private static readonly HashSet<string> UserActiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "ts3client_win32", "mumble", "Skype", "Teams", "ms-teams",
        "Zoom", "Slack", "Telegram", "WhatsApp", "obs64", "obs32", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "ShareX",
        "Medal", "Spotify", "AppleMusic", "iTunes", "foobar2000", "vlc", "MusicBee", "TIDAL", "Deezer", "AIMP", "voicemeeter", "voicemeeterpro",
        "voicemeeter8", "SteelSeriesSonar", "SteelSeriesGG", "NVIDIA Broadcast",
    };

    public static string Clean(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static AppClass Classify(string processName)
    {
        var name = Clean(processName);
        if (name.Length == 0 || SystemNames.Contains(name)) return AppClass.System;
        // Voice and recording tools first: "NVIDIA Broadcast" is yours to decide on, the rest of NVIDIA's tools are not.
        if (UserActiveNames.Contains(name)) return AppClass.UserActive;
        if (CompanionNames.Contains(name) || CompanionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return AppClass.Companion;
        return AppClass.Background;
    }

    /// <summary>True when the guard may act on this app at all.</summary>
    public static bool MayTouch(string processName) => Classify(processName) is AppClass.Background or AppClass.UserActive;
}

// ---------------------------------------------------------------- launch plan

public sealed record PlannedAction(AppUsage App, FreeAction Action, string Reason, bool Remembered = false);

/// <summary>An optional step shown before launch. <see cref="Default"/> is what's pre-selected (null = leave it running).</summary>
public sealed record ResourceSuggestion(AppUsage App, IReadOnlyList<FreeAction> Options, FreeAction? Default, string Why, string? HistoryNote);

public sealed record LaunchPlan(IReadOnlyList<PlannedAction> Automatic, IReadOnlyList<ResourceSuggestion> Suggestions);

public static class ResourcePlanner
{
    public const int MaxSuggestions = 6;

    /// <summary>Adds up processes by app. Apps with a protected process (the game, the launcher, a trainer) are left out whole.</summary>
    public static List<AppUsage> Group(IEnumerable<ProcessUsage> usages, IReadOnlySet<int> protectedPids) =>
        [.. usages.GroupBy(u => AppClassifier.Clean(u.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Any(u => protectedPids.Contains(u.Pid)))
            .Select(g => new AppUsage(g.Key, [.. g.Select(u => u.Pid)], g.Sum(u => u.CpuPercent), g.Sum(u => u.RamMb),
                Math.Min(100, g.Sum(u => u.GpuPercent)), g.Sum(u => u.VramMb), g.Any(u => u.HasWindow)))];

    /// <summary>Memory is tight when under 2 GB or 15 % is free.</summary>
    public static bool MemoryTight(MachineState m) => m.TotalRamMb > 0 && m.FreeRamMb < Math.Max(2048, m.TotalRamMb * 0.15);

    public static LaunchPlan PlanLaunch(IReadOnlyList<AppUsage> apps, MachineState machine, ResourceMemory memory, bool askAtLaunch = true)
    {
        var automatic = new List<PlannedAction>();
        var suggestions = new List<ResourceSuggestion>();
        var tight = MemoryTight(machine);
        foreach (var app in apps.OrderByDescending(a => a.Impact))
        {
            var cls = AppClassifier.Classify(app.Name);
            if (cls is AppClass.System or AppClass.Companion) continue;
            var rule = memory.RuleFor(app.Name);
            if (rule is { Choice: ResourceChoice.Never }) continue; // "never touch this app" means exactly that
            if (rule is { Choice: ResourceChoice.Always, Action: { } remembered })
            {
                if (remembered != FreeAction.Close || app.HasWindow)
                    automatic.Add(new PlannedAction(app, remembered, $"{Verb(remembered)} {app.Name}: your saved choice", Remembered: true));
                continue;
            }

            if (cls == AppClass.Background)
            {
                if ((app.CpuPercent >= 2 || app.GpuPercent >= 2) && !memory.Blocked(app.Name, FreeAction.Restrain))
                    automatic.Add(new PlannedAction(app, FreeAction.Restrain, $"Held back {app.Name} ({Describe(app)})"));
                if (tight && app.RamMb >= 300 && !memory.Blocked(app.Name, FreeAction.TrimMemory))
                    automatic.Add(new PlannedAction(app, FreeAction.TrimMemory, $"Freed idle memory from {app.Name} ({Gb(app.RamMb)})"));
            }

            var offender = memory.OffenderFor(app.Name);
            var heavy = app.GpuPercent >= 5 || app.VramMb >= 300 || app.CpuPercent >= 5 || app.RamMb >= (tight ? 500 : 1000);
            if (!askAtLaunch || suggestions.Count >= MaxSuggestions || !(heavy || offender is { Count: >= 2 })) continue;

            var options = new List<FreeAction> { FreeAction.Pause };
            if (app.HasWindow) options.Add(FreeAction.Close);
            var (def, note) = DefaultFor(app, cls, options, memory, offender);
            suggestions.Add(new ResourceSuggestion(app, options, def, "Using " + Describe(app), note));
        }
        return new LaunchPlan(automatic, suggestions);
    }

    private static (FreeAction? Default, string? Note) DefaultFor(AppUsage app, AppClass cls, List<FreeAction> options, ResourceMemory memory, ResourceOffender? offender)
    {
        // What helped before wins; what made things worse is never pre-selected.
        var best = options.Select(a => memory.OutcomeFor(app.Name, a)).Where(o => o is { Helped: > 0 } && o.Helped > o.Hurt).MaxBy(o => o!.Helped - o.Hurt);
        if (best is not null) return (best.Action, $"{Verb(best.Action)} it helped before ({Gain(best)}).");
        var worse = options.Select(a => memory.OutcomeFor(app.Name, a)).FirstOrDefault(o => o is { Hurt: > 0, Helped: 0 });
        if (worse is not null) return (null, $"{Verb(worse.Action)} it made the game run worse last time, so it is left alone unless you choose otherwise.");
        var note = offender is null ? null : $"It was busy ({offender.Kind}) while the game was struggling {Times(offender.Count)}.";
        if (cls == AppClass.UserActive) return (null, note ?? "You may be using this while you play, so it is left alone unless you choose otherwise.");
        return (FreeAction.Pause, note);
    }

    public static string Verb(FreeAction a) => a switch
    {
        FreeAction.Restrain => "Holding back",
        FreeAction.TrimMemory => "Freeing memory from",
        FreeAction.Pause => "Pausing",
        _ => "Closing",
    };

    public static string Describe(AppUsage a)
    {
        var parts = new List<string>();
        if (a.GpuPercent >= 1) parts.Add($"{a.GpuPercent:0}% of the GPU");
        if (a.VramMb >= 100) parts.Add($"{Gb(a.VramMb)} of video memory");
        if (a.CpuPercent >= 1) parts.Add($"{a.CpuPercent:0}% of the CPU");
        if (a.RamMb >= 100) parts.Add($"{Gb(a.RamMb)} of memory");
        return parts.Count == 0 ? "very little right now" : string.Join(", ", parts);
    }

    public static string Gain(ResourceOutcome o)
    {
        var parts = new List<string>();
        if (Math.Abs(o.LowGainPercent) >= 1) parts.Add($"1% low {o.LowGainPercent:+0;-0}%");
        if (Math.Abs(o.FpsGainPercent) >= 1) parts.Add($"FPS {o.FpsGainPercent:+0;-0}%");
        if (Math.Abs(o.TempDropC) >= 1) parts.Add(o.TempDropC > 0 ? $"{o.TempDropC:0} °C cooler" : $"{-o.TempDropC:0} °C hotter");
        return parts.Count == 0 ? "no clear change" : string.Join(", ", parts);
    }

    private static string Gb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";
    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : $"{n} times";
}

// ---------------------------------------------------------------- did it help?

/// <summary>How the game ran over a stretch of time.</summary>
public sealed record PerfWindow(double Fps, double LowFps, double? TempC, double StuttersPerMinute);

public static class ResourceJudge
{
    /// <summary>
    /// Worse: FPS down 5 %, 1% low down 10 %, or clearly more stutter. Better: 1% low up 5 %, FPS up 3 %,
    /// stutter halved, or 2 °C cooler without losing frames. Anything smaller is noise.
    /// </summary>
    /// <param name="scale">2 for comparisons across sessions: the thresholds are doubled.</param>
    public static OutcomeVerdict Judge(PerfWindow before, PerfWindow after, double scale = 1)
    {
        if (before.Fps <= 0 || after.Fps <= 0) return OutcomeVerdict.NoChange;
        var fps = (after.Fps - before.Fps) / before.Fps;
        var low = before.LowFps > 0 && after.LowFps > 0 ? (after.LowFps - before.LowFps) / before.LowFps : 0;
        var moreStutter = after.StuttersPerMinute >= before.StuttersPerMinute + 3 * scale && after.StuttersPerMinute >= before.StuttersPerMinute * (1 + 0.5 * scale);
        if (fps <= -0.05 * scale || low <= -0.10 * scale || moreStutter) return OutcomeVerdict.Hurt;
        var lessStutter = before.StuttersPerMinute >= 2 * scale && after.StuttersPerMinute <= before.StuttersPerMinute / (1 + scale);
        var cooler = before.TempC is { } b && after.TempC is { } a && b - a >= 2 * scale && fps > -0.02;
        return low >= 0.05 * scale || fps >= 0.03 * scale || lessStutter || cooler ? OutcomeVerdict.Helped : OutcomeVerdict.NoChange;
    }
}

// ---------------------------------------------------------------- while playing

/// <summary>One reading while the game runs. <see cref="Stutters"/> counts new hitches since the previous reading;
/// <see cref="TargetFps"/> is the cap in force right now (it moves when the thermal guard or the idle cap steps in).</summary>
public sealed record PerfSample(double Fps, double LowFps, double? TempC, int Stutters, int? TargetFps = null);

public sealed record LiveUndo(string Name, FreeAction Action);

public sealed record LiveResult(IReadOnlyList<PlannedAction> Apply, IReadOnlyList<LiveUndo> Undo, IReadOnlyList<string> Notes)
{
    public static readonly LiveResult Nothing = new([], [], []);
}

/// <summary>
/// Watches background apps during a session. An app that keeps using the GPU or CPU is held back (at once
/// when the game is struggling, after three readings otherwise); idle memory is freed when RAM runs low.
/// Each step is then checked against how the game ran before it: steps that made things worse are undone
/// and remembered, steps that helped are remembered too. One step at a time, so the result can be told apart.
/// </summary>
public sealed class ResourceGuardLive(ResourceMemory memory, string? gameName)
{
    public const double BusyPercent = 8;
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan TrialLength = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan BeforeWindow = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan TrimEvery = TimeSpan.FromSeconds(90);

    private sealed record Trial(string Name, FreeAction Action, DateTimeOffset At, PerfWindow Before);

    private readonly List<(DateTimeOffset At, PerfSample S)> _history = [];
    private readonly Dictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restrained = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offendersNoted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTrim = new(StringComparer.OrdinalIgnoreCase);
    private Trial? _trial;

    /// <summary>Apps held back so far (the caller keeps new processes of these apps held back too).</summary>
    public IReadOnlyCollection<string> Restrained => _restrained;

    /// <summary>Tell the guard about apps already held back at launch.</summary>
    public void MarkRestrained(string name) => _restrained.Add(name);

    public bool Struggling(PerfSample s)
    {
        if (s.Fps <= 0) return false;
        if (s.Stutters >= 2) return true;
        if (s.TargetFps is { } t and > 0) return s.Fps < t * 0.93 || (s.LowFps > 0 && s.LowFps < t * 0.8);
        var typical = _history.Count >= 5 ? _history.Select(h => h.S.Fps).Order().ElementAt(_history.Count / 2) : 0;
        return typical > 0 && s.Fps < typical * 0.85;
    }

    public LiveResult Tick(DateTimeOffset now, IReadOnlyList<AppUsage> apps, MachineState machine, PerfSample perf)
    {
        var apply = new List<PlannedAction>();
        var undo = new List<LiveUndo>();
        var notes = new List<string>();
        var struggling = Struggling(perf);
        _history.Add((now, perf));
        _history.RemoveAll(h => now - h.At > TimeSpan.FromSeconds(90));

        // Check the step that is on trial.
        if (_trial is { } t && now - t.At >= TrialLength)
        {
            _trial = null;
            // A cap change during the trial (thermal guard, idle cap, quick switch) moves the FPS on its own: no verdict.
            var capMoved = _history.Where(h => h.At >= t.At - BeforeWindow).Select(h => h.S.TargetFps).Distinct().Count() > 1;
            if (!capMoved && Window(t.At + Settle, now) is { } after)
            {
                var verdict = ResourceJudge.Judge(t.Before, after);
                var o = memory.Record(t.Name, t.Action, verdict, t.Before, after, gameName, now);
                if (verdict == OutcomeVerdict.Hurt)
                {
                    _blockedThisSession.Add(Key(t.Name, t.Action));
                    if (t.Action == FreeAction.Restrain) { undo.Add(new LiveUndo(t.Name, t.Action)); _restrained.Remove(t.Name); }
                    notes.Add($"{ResourcePlanner.Verb(t.Action)} {t.Name} made the game run worse ({Change(t.Before, after)}), so it was undone and noted" +
                              (memory.Blocked(t.Name, t.Action) ? "; it won't be tried on its own again." : "."));
                }
                else
                    notes.Add($"{ResourcePlanner.Verb(t.Action)} {t.Name}: {(verdict == OutcomeVerdict.Helped ? "helped" : "no clear change")} ({Change(t.Before, after)}); {o.Helped} of {o.Tries} tries have helped.");
            }
        }

        foreach (var gone in _strikes.Keys.Where(k => !apps.Any(a => string.Equals(a.Name, k, StringComparison.OrdinalIgnoreCase))).ToList()) _strikes.Remove(gone);

        foreach (var app in apps.OrderByDescending(a => a.Impact))
        {
            var busyGpu = app.GpuPercent >= BusyPercent;
            var busyCpu = app.CpuPercent >= BusyPercent;
            if (!busyGpu && !busyCpu) { _strikes.Remove(app.Name); continue; }
            var cls = AppClassifier.Classify(app.Name);
            if (cls is AppClass.System or AppClass.Companion) continue;
            var rule = memory.RuleFor(app.Name);
            if (rule is { Choice: ResourceChoice.Never }) continue;

            // Busy while the game struggles: remember it, so it is offered at the next launch.
            if (struggling && _offendersNoted.Add(app.Name)) memory.NoteOffender(app.Name, busyGpu ? "GPU" : "CPU", now);

            if (cls != AppClass.Background || _restrained.Contains(app.Name)) continue;
            var strikes = _strikes[app.Name] = _strikes.GetValueOrDefault(app.Name) + 1;
            if (strikes < (struggling ? 1 : 3) || _trial is not null) continue;
            if (memory.Blocked(app.Name, FreeAction.Restrain) || _blockedThisSession.Contains(Key(app.Name, FreeAction.Restrain))) continue;
            if (Window(now - BeforeWindow, now) is not { } before) continue;
            _restrained.Add(app.Name);
            _trial = new Trial(app.Name, FreeAction.Restrain, now, before);
            apply.Add(new PlannedAction(app, FreeAction.Restrain, $"Held back {app.Name} ({ResourcePlanner.Describe(app)})"));
        }

        // Memory running out: under 1 GB or 8 % free.
        if (_trial is null && machine.TotalRamMb > 0 && machine.FreeRamMb < Math.Max(1024, machine.TotalRamMb * 0.08) && Window(now - BeforeWindow, now) is { } beforeTrim)
        {
            var app = apps.Where(a => a.RamMb >= 300 && AppClassifier.Classify(a.Name) == AppClass.Background
                                      && memory.RuleFor(a.Name) is not { Choice: ResourceChoice.Never }
                                      && !memory.Blocked(a.Name, FreeAction.TrimMemory) && !_blockedThisSession.Contains(Key(a.Name, FreeAction.TrimMemory))
                                      && (!_lastTrim.TryGetValue(a.Name, out var last) || now - last >= TrimEvery))
                .MaxBy(a => a.RamMb);
            if (app is not null)
            {
                _lastTrim[app.Name] = now;
                if (struggling && _offendersNoted.Add(app.Name)) memory.NoteOffender(app.Name, "memory", now);
                _trial = new Trial(app.Name, FreeAction.TrimMemory, now, beforeTrim);
                apply.Add(new PlannedAction(app, FreeAction.TrimMemory, $"Memory was running low: freed idle memory from {app.Name} ({app.RamMb} MB)"));
            }
        }
        return apply.Count + undo.Count + notes.Count == 0 ? LiveResult.Nothing : new LiveResult(apply, undo, notes);
    }

    private PerfWindow? Window(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = _history.Where(h => h.At > from && h.At <= to && h.S.Fps > 0).ToList();
        if (rows.Count < 2) return null;
        var temps = rows.Where(r => r.S.TempC is not null).Select(r => r.S.TempC!.Value).ToList();
        var lows = rows.Where(r => r.S.LowFps > 0).Select(r => r.S.LowFps).ToList();
        var minutes = Math.Max((rows[^1].At - rows[0].At).TotalMinutes, 4 / 60.0);
        return new PerfWindow(rows.Average(r => r.S.Fps), lows.Count > 0 ? lows.Average() : 0, temps.Count > 0 ? temps.Average() : null,
            rows.Skip(1).Sum(r => r.S.Stutters) / minutes);
    }

    private static string Key(string name, FreeAction action) => name.ToLowerInvariant() + "|" + action;

    private static string Change(PerfWindow before, PerfWindow after)
    {
        var text = $"FPS {before.Fps:0} → {after.Fps:0}";
        if (before.LowFps > 0 && after.LowFps > 0) text += $", 1% low {before.LowFps:0} → {after.LowFps:0}";
        if (before.TempC is { } b && after.TempC is { } a) text += $", {b:0} → {a:0} °C";
        return text;
    }
}
