using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Tests;

public class ResourceGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);
    private static readonly MachineState Roomy = new(32768, 16000);
    private static AppUsage App(string name, double cpu = 0, double gpu = 0, long ram = 200, long vram = 0, bool window = false) =>
        new(name, [name.GetHashCode()], cpu, ram, gpu, vram, window);

    [Theory]
    [InlineData("svchost.exe", AppClass.System)]
    [InlineData("MsMpEng", AppClass.System)]
    [InlineData("steamwebhelper", AppClass.Companion)]
    [InlineData("RTSS.exe", AppClass.Companion)]
    [InlineData("EasyAntiCheat_EOS", AppClass.Companion)]
    [InlineData("FitzLauncher", AppClass.Companion)]
    [InlineData("Discord.exe", AppClass.UserActive)]
    [InlineData("obs64", AppClass.UserActive)]
    [InlineData("chrome.exe", AppClass.Background)]
    public void Classifies(string name, AppClass expected) => Assert.Equal(expected, AppClassifier.Classify(name));

    [Fact]
    public void GroupLeavesOutAppsThatOwnAProtectedProcess()
    {
        var apps = ResourcePlanner.Group([new(1, "game", 30, 4000, 90, 6000), new(2, "chrome", 5, 400, 2, 100), new(3, "chrome", 3, 300, 1, 50)], new HashSet<int> { 1 });
        var chrome = Assert.Single(apps);
        Assert.Equal(8, chrome.CpuPercent);
        Assert.Equal(700, chrome.RamMb);
        Assert.Equal(2, chrome.Pids.Count);
    }

    [Fact]
    public void SystemAndCompanionAppsAreNeverPlanned()
    {
        var plan = ResourcePlanner.PlanLaunch([App("svchost", 40, 40), App("steamwebhelper", 30, 20, 2000), App("RTSS", 10)], Roomy, new ResourceMemory());
        Assert.Empty(plan.Automatic);
        Assert.Empty(plan.Suggestions);
    }

    [Fact]
    public void BusyBackgroundAppIsHeldBackAutomatically_QuietOneIsNot()
    {
        var plan = ResourcePlanner.PlanLaunch([App("chrome", 6, 3), App("notepad", 0.1)], Roomy, new ResourceMemory());
        var a = Assert.Single(plan.Automatic);
        Assert.Equal(("chrome", FreeAction.Restrain), (a.App.Name, a.Action));
    }

    [Fact]
    public void IdleMemoryIsTrimmedOnlyWhenMemoryIsTight()
    {
        var big = App("chrome", 0.5, 0, 2000);
        Assert.DoesNotContain(ResourcePlanner.PlanLaunch([big], Roomy, new ResourceMemory()).Automatic, a => a.Action == FreeAction.TrimMemory);
        Assert.Contains(ResourcePlanner.PlanLaunch([big], new MachineState(16384, 1500), new ResourceMemory()).Automatic, a => a.Action == FreeAction.TrimMemory);
    }

    [Fact]
    public void HeavyAppIsOnlySuggested_PauseIsDefaultForBackground_UserActiveIsLeftAlone()
    {
        var plan = ResourcePlanner.PlanLaunch([App("onedrive", 1, 0, 1500), App("Discord", 2, 12, 600, 400, window: true)], Roomy, new ResourceMemory());
        var heavy = plan.Suggestions.Single(s => s.App.Name == "onedrive");
        Assert.Equal(FreeAction.Pause, heavy.Default);
        Assert.DoesNotContain(FreeAction.Close, heavy.Options); // no window, so there is nothing to close
        var voice = plan.Suggestions.Single(s => s.App.Name == "Discord");
        Assert.Null(voice.Default);
        Assert.Contains(FreeAction.Close, voice.Options);
    }

    [Fact]
    public void NeverRuleSkipsTheAppEntirely_AlwaysRuleRepeatsTheChoice()
    {
        var memory = new ResourceMemory();
        memory.Remember("chrome", ResourceChoice.Never, null, T0);
        memory.Remember("onedrive", ResourceChoice.Always, FreeAction.Pause, T0);
        var plan = ResourcePlanner.PlanLaunch([App("chrome", 20, 30, 3000), App("onedrive", 0, 0, 100)], Roomy, memory);
        Assert.DoesNotContain(plan.Automatic, a => a.App.Name == "chrome");
        Assert.DoesNotContain(plan.Suggestions, s => s.App.Name == "chrome");
        var saved = Assert.Single(plan.Automatic);
        Assert.Equal((FreeAction.Pause, true), (saved.Action, saved.Remembered));
    }

    [Fact]
    public void AlwaysCloseWithoutAWindowIsSkipped()
    {
        var memory = new ResourceMemory();
        memory.Remember("updater", ResourceChoice.Always, FreeAction.Close, T0);
        Assert.Empty(ResourcePlanner.PlanLaunch([App("updater", 5)], Roomy, memory).Automatic);
    }

    [Fact]
    public void RememberReplacesAndAskClearsTheRule()
    {
        var memory = new ResourceMemory();
        memory.Remember("x", ResourceChoice.Always, FreeAction.Pause, T0);
        memory.Remember("x", ResourceChoice.Never, null, T0);
        Assert.Equal(ResourceChoice.Never, Assert.Single(memory.Rules).Choice);
        memory.Remember("x", ResourceChoice.Ask, null, T0);
        Assert.Empty(memory.Rules);
    }

    [Fact]
    public void SuggestionsAreCapped()
    {
        var apps = Enumerable.Range(0, 12).Select(i => App("app" + i, 6, 6, 1500)).ToList();
        Assert.Equal(ResourcePlanner.MaxSuggestions, ResourcePlanner.PlanLaunch(apps, Roomy, new ResourceMemory()).Suggestions.Count);
        Assert.Empty(ResourcePlanner.PlanLaunch(apps, Roomy, new ResourceMemory(), askAtLaunch: false).Suggestions);
    }

    [Fact]
    public void HistoryDecidesTheDefault()
    {
        var memory = new ResourceMemory();
        var before = new PerfWindow(60, 40, 80, 3);
        memory.Record("chrome", FreeAction.Pause, OutcomeVerdict.Helped, before, new PerfWindow(66, 52, 76, 1), "Game", T0);
        var s = Assert.Single(ResourcePlanner.PlanLaunch([App("chrome", 1, 0, 1500)], Roomy, memory).Suggestions);
        Assert.Equal(FreeAction.Pause, s.Default);
        Assert.Contains("helped before", s.HistoryNote);

        memory.Record("slack", FreeAction.Pause, OutcomeVerdict.Hurt, before, new PerfWindow(50, 30, 80, 8), "Game", T0);
        var w = Assert.Single(ResourcePlanner.PlanLaunch([App("slack", 1, 0, 1500)], Roomy, memory).Suggestions);
        Assert.Null(w.Default);
        Assert.Contains("worse", w.HistoryNote);
    }

    [Fact]
    public void TwiceWorseAndNeverBetterBlocksAutomaticUse()
    {
        var memory = new ResourceMemory();
        var b = new PerfWindow(60, 50, null, 0);
        memory.Record("chrome", FreeAction.Restrain, OutcomeVerdict.Hurt, b, new PerfWindow(50, 40, null, 0), null, T0);
        Assert.False(memory.Blocked("chrome", FreeAction.Restrain));
        memory.Record("chrome", FreeAction.Restrain, OutcomeVerdict.Hurt, b, new PerfWindow(50, 40, null, 0), null, T0);
        Assert.True(memory.Blocked("chrome", FreeAction.Restrain));
        Assert.DoesNotContain(ResourcePlanner.PlanLaunch([App("chrome", 10, 10)], Roomy, memory).Automatic, a => a.Action == FreeAction.Restrain);
    }

    [Fact]
    public void RecordKeepsRunningAverages()
    {
        var memory = new ResourceMemory();
        memory.Record("a", FreeAction.Pause, OutcomeVerdict.Helped, new PerfWindow(100, 50, 80, 0), new PerfWindow(110, 60, 76, 0), null, T0);
        var o = memory.Record("a", FreeAction.Pause, OutcomeVerdict.NoChange, new PerfWindow(100, 50, 80, 0), new PerfWindow(100, 50, 80, 0), null, T0);
        Assert.Equal((1, 1, 0), (o.Helped, o.NoChange, o.Hurt));
        Assert.Equal(5, o.FpsGainPercent, 3);
        Assert.Equal(10, o.LowGainPercent, 3);
        Assert.Equal(2, o.TempDropC, 3);
    }

    [Fact]
    public void JudgeSeparatesRealChangeFromNoise()
    {
        var b = new PerfWindow(60, 45, 80, 2);
        Assert.Equal(OutcomeVerdict.Helped, ResourceJudge.Judge(b, new PerfWindow(60, 50, 80, 2)));          // 1% low +11 %
        Assert.Equal(OutcomeVerdict.Helped, ResourceJudge.Judge(b, new PerfWindow(60, 45, 77, 2)));          // 3 °C cooler, same frames
        Assert.Equal(OutcomeVerdict.Helped, ResourceJudge.Judge(b, new PerfWindow(60, 45, 80, 0.5)));        // stutter cut by more than half
        Assert.Equal(OutcomeVerdict.NoChange, ResourceJudge.Judge(b, new PerfWindow(60.5, 45.5, 79.5, 2)));
        Assert.Equal(OutcomeVerdict.Hurt, ResourceJudge.Judge(b, new PerfWindow(56, 45, 80, 2)));           // FPS -6.7 %
        Assert.Equal(OutcomeVerdict.Hurt, ResourceJudge.Judge(b, new PerfWindow(60, 38, 80, 2)));           // 1% low -15 %
        Assert.Equal(OutcomeVerdict.Hurt, ResourceJudge.Judge(b, new PerfWindow(60, 45, 80, 9)));           // much more stutter
        Assert.Equal(OutcomeVerdict.NoChange, ResourceJudge.Judge(new PerfWindow(0, 0, null, 0), b));
    }

    // ---- live guard

    private static (ResourceGuardLive Guard, ResourceMemory Memory) Live(int? target = 60)
    {
        var m = new ResourceMemory();
        return (new ResourceGuardLive(m, "Game"), m);
    }

    private static PerfSample Perf(double fps, double low = 0, double? temp = 70, int stutters = 0, int? target = 60) => new(fps, low, temp, stutters, target);

    /// <summary>Feeds one reading a second with the given app list until <paramref name="seconds"/> pass; returns everything it asked for.</summary>
    private static List<LiveResult> Run(ResourceGuardLive g, ref DateTimeOffset now, int seconds, Func<int, PerfSample> perf, Func<int, IReadOnlyList<AppUsage>> apps, MachineState? machine = null)
    {
        var results = new List<LiveResult>();
        for (var i = 0; i < seconds; i++)
        {
            now = now.AddSeconds(1);
            results.Add(g.Tick(now, apps(i), machine ?? Roomy, perf(i)));
        }
        return results;
    }

    [Fact]
    public void BusyAppWhileGameStrugglesIsHeldBackAtOnce_AfterEnoughHistory()
    {
        var (g, memory) = Live();
        var now = T0;
        var results = Run(g, ref now, 30, _ => Perf(45, 30), _ => [App("chrome", 3, 20)]);
        var first = results.SelectMany(r => r.Apply).First();
        Assert.Equal(("chrome", FreeAction.Restrain), (first.App.Name, first.Action));
        Assert.Contains(memory.Offenders, o => o.Name == "chrome" && o.Kind == "GPU");
        Assert.Single(results.SelectMany(r => r.Apply)); // one step at a time
    }

    [Fact]
    public void BusyAppWhileGameIsFineWaitsForThreeReadings()
    {
        var (g, memory) = Live();
        var now = T0;
        var results = Run(g, ref now, 25, _ => Perf(60, 55), _ => [App("chrome", 20, 2)]);
        Assert.NotEmpty(results.SelectMany(r => r.Apply));
        Assert.Empty(memory.Offenders); // the game wasn't struggling, so nothing is blamed
    }

    [Fact]
    public void QuietAppsCompanionsAndNeverRulesAreLeftAlone()
    {
        var (g, memory) = Live();
        memory.Remember("chrome", ResourceChoice.Never, null, T0);
        var now = T0;
        var results = Run(g, ref now, 40, _ => Perf(40, 20), _ => [App("chrome", 30, 30), App("steamwebhelper", 30, 30), App("svchost", 30, 30), App("notepad", 1, 1)]);
        Assert.Empty(results.SelectMany(r => r.Apply));
    }

    [Fact]
    public void StepThatMadeThingsWorseIsUndoneAndRemembered()
    {
        var (g, memory) = Live();
        var now = T0;
        // Fine at 60 FPS for 20 s, with chrome busy, so a step is taken once chrome has been busy for 3 readings…
        var results = Run(g, ref now, 12, _ => Perf(60, 55), _ => [App("chrome", 20, 2)]);
        Assert.Single(results.SelectMany(r => r.Apply));
        // …then the game gets worse afterwards.
        var after = Run(g, ref now, 30, _ => Perf(50, 35), _ => [App("chrome", 2, 0)]);
        var undo = Assert.Single(after.SelectMany(r => r.Undo));
        Assert.Equal("chrome", undo.Name);
        Assert.Equal(1, memory.OutcomeFor("chrome", FreeAction.Restrain)!.Hurt);
        Assert.Contains(after.SelectMany(r => r.Notes), n => n.Contains("worse"));
        // The same step is not tried again this session.
        var again = Run(g, ref now, 30, _ => Perf(50, 35), _ => [App("chrome", 20, 20)]);
        Assert.Empty(again.SelectMany(r => r.Apply));
    }

    [Fact]
    public void StepThatHelpedIsKept()
    {
        var (g, memory) = Live();
        var now = T0;
        Run(g, ref now, 12, i => Perf(50, 38), _ => [App("chrome", 20, 2)]);
        var after = Run(g, ref now, 30, _ => Perf(60, 52), _ => [App("chrome", 1, 0)]);
        Assert.Empty(after.SelectMany(r => r.Undo));
        Assert.Equal(1, memory.OutcomeFor("chrome", FreeAction.Restrain)!.Helped);
    }

    [Fact]
    public void ACapChangeDuringATrialGivesNoVerdict()
    {
        var (g, memory) = Live();
        var now = T0;
        Run(g, ref now, 12, _ => Perf(60, 55), _ => [App("chrome", 20, 2)]);
        // The thermal guard lowers the cap, so FPS falls on its own.
        var after = Run(g, ref now, 30, _ => Perf(45, 40, target: 45), _ => [App("chrome", 1, 0)]);
        Assert.Empty(after.SelectMany(r => r.Undo));
        Assert.Null(memory.OutcomeFor("chrome", FreeAction.Restrain));
    }

    [Fact]
    public void LowMemoryTrimsTheBiggestBackgroundApp_NotTooOften()
    {
        var (g, _) = Live();
        var now = T0;
        var tight = new MachineState(16384, 600);
        var results = Run(g, ref now, 40, _ => Perf(60, 55), _ => [App("chrome", 0, 0, 2500), App("slack", 0, 0, 900)], tight);
        var trims = results.SelectMany(r => r.Apply).Where(a => a.Action == FreeAction.TrimMemory).ToList();
        Assert.NotEmpty(trims);
        Assert.All(trims, t => Assert.Equal("chrome", t.App.Name));
        Assert.Single(trims); // a second one waits for the first to be judged and for the pause between trims
    }

    [Fact]
    public void NothingHappensWithoutAnyFrameData()
    {
        var (g, _) = Live();
        var now = T0;
        Assert.Empty(Run(g, ref now, 30, _ => Perf(0), _ => [App("chrome", 30, 30)]).SelectMany(r => r.Apply));
    }

    // ---- across sessions (launch steps)

    private static readonly (string, FreeAction)[] PauseChrome = [("chrome", FreeAction.Pause)];

    [Fact]
    public void FirstSessionOnlyBecomesTheBaseline()
    {
        var m = new ResourceMemory();
        Assert.Null(m.JudgeLaunch("g", PauseChrome, new PerfWindow(60, 50, 70, 0), T0));
        Assert.Contains("chrome|Pause", m.LastRuns["g"].Actions);
    }

    [Fact]
    public void OneAddedStepThatClearlyHurtsIsRecordedAsWorse()
    {
        var m = new ResourceMemory();
        m.JudgeLaunch("g", [], new PerfWindow(60, 50, 70, 0), T0);
        var r = m.JudgeLaunch("g", PauseChrome, new PerfWindow(52, 40, 70, 0), T0.AddDays(1));
        Assert.Equal(("chrome", FreeAction.Pause, OutcomeVerdict.Hurt), r);
        Assert.Equal(1, m.OutcomeFor("chrome", FreeAction.Pause)!.Hurt);
    }

    [Fact]
    public void SmallDifferencesAcrossSessionsAreNotBlamedOnTheStep()
    {
        var m = new ResourceMemory();
        m.JudgeLaunch("g", [], new PerfWindow(60, 50, 70, 0), T0);
        var r = m.JudgeLaunch("g", PauseChrome, new PerfWindow(57, 46, 70, 0), T0.AddDays(1)); // -5 % FPS, -8 % low: scene noise
        Assert.Equal(OutcomeVerdict.NoChange, r!.Value.Verdict);
    }

    [Fact]
    public void DroppingAStepWhenTheGameGotBetterMeansItWasCostingSomething()
    {
        var m = new ResourceMemory();
        m.JudgeLaunch("g", PauseChrome, new PerfWindow(50, 38, 70, 0), T0);
        var r = m.JudgeLaunch("g", [], new PerfWindow(60, 50, 70, 0), T0.AddDays(1));
        Assert.Equal(("chrome", FreeAction.Pause, OutcomeVerdict.Hurt), r);
    }

    [Fact]
    public void SeveralStepsChangingAtOnceConcludesNothing_AndOldRunsAreIgnored()
    {
        var m = new ResourceMemory();
        m.JudgeLaunch("g", [], new PerfWindow(60, 50, 70, 0), T0);
        Assert.Null(m.JudgeLaunch("g", [("a", FreeAction.Pause), ("b", FreeAction.Pause)], new PerfWindow(30, 20, 70, 0), T0.AddDays(1)));
        Assert.Null(m.JudgeLaunch("g", [], new PerfWindow(60, 50, 70, 0), T0.AddDays(1) + ResourceMemory.CompareWithin + TimeSpan.FromDays(1)));
    }
}
