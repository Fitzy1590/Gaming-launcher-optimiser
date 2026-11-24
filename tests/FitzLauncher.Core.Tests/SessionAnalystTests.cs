using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Tests;

public class SessionAnalystTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Two-minute session of 2 s samples from 40 s in; <paramref name="shape"/> edits sample i (seconds into the session).</summary>
    private static List<ContextSample> Session(Func<int, ContextSample, ContextSample>? shape = null, int seconds = 240)
    {
        var list = new List<ContextSample>();
        for (var t = 40; t < seconds; t += 2)
        {
            var s = new ContextSample(T0.AddSeconds(t), 60, 62, 50, 70, 40, null, 0, false, 10, false, 60, 15);
            list.Add(shape?.Invoke(t, s) ?? s);
        }
        return list;
    }

    private static Game G(int? target = 60) => new() { Name = "Test", FpsTarget = target };
    private static SessionAnalysis Run(Game g, List<ContextSample> s) => SessionAnalyst.Analyze(g, T0, s, 90, 85);

    [Fact]
    public void Steady_session_has_no_findings()
    {
        var a = Run(G(), Session());
        Assert.Empty(a.Findings);
        Assert.Contains("No frame drops", a.Summary);
    }

    [Fact]
    public void Short_dips_under_six_seconds_are_ignored()
    {
        var a = Run(G(), Session((t, s) => t is >= 100 and < 104 ? s with { Fps = 30 } : s));
        Assert.Empty(a.Findings);
    }

    [Fact]
    public void Sustained_drop_with_gpu_maxed_and_cpu_free_is_gpu_bound_with_both_kinds_of_advice()
    {
        var a = Run(G(), Session((t, s) => t is >= 100 and < 130 ? s with { Fps = 40, GpuLoad = 99, CpuLoad = 55 } : s));
        var f = Assert.Single(a.Findings);
        Assert.Equal(AnalysisKind.FrameDrop, f.Kind);
        Assert.Equal(AnalysisCause.GpuBound, f.Cause);
        Assert.Contains(f.Suggestions, x => x.Scope == SuggestionScope.InGame);
        Assert.Contains(f.Suggestions, x => x.Scope == SuggestionScope.OutOfGame);
        Assert.True(f.Seconds >= 28);
    }

    [Fact]
    public void Drop_with_cpu_maxed_is_cpu_bound()
    {
        var f = Assert.Single(Run(G(), Session((t, s) => t is >= 100 and < 120 ? s with { Fps = 35, CpuLoad = 98, GpuLoad = 60 } : s)).Findings);
        Assert.Equal(AnalysisCause.CpuBound, f.Cause);
    }

    [Theory]
    [InlineData("thermal", AnalysisCause.Overheating)]
    [InlineData("ram", AnalysisCause.MemoryFull)]
    [InlineData("disk", AnalysisCause.DriveBusy)]
    [InlineData("app", AnalysisCause.BackgroundApp)]
    [InlineData("power", AnalysisCause.PowerLimitChange)]
    [InlineData("none", AnalysisCause.GameSide)]
    public void Drop_cause_follows_what_else_was_going_on(string what, AnalysisCause expected)
    {
        var a = Run(G(), Session((t, s) =>
        {
            if (t is < 100 or >= 120) return s;
            s = s with { Fps = 30 };
            return what switch
            {
                "thermal" => s with { ThermalThrottling = true },
                "ram" => s with { FreeRamPercent = 4 },
                "disk" => s with { DiskBusyPercent = 98 },
                "app" => s with { TopBackgroundApp = "Chrome", TopBackgroundCpu = 30 },
                "power" => s with { PowerLimitChanged = true },
                _ => s,
            };
        }));
        Assert.Equal(expected, Assert.Single(a.Findings).Cause);
    }

    [Fact]
    public void Warm_up_period_is_left_out()
    {
        var early = Session((t, s) => t < 60 ? s with { Fps = 10 } : s).Select(s => s with { At = s.At.AddSeconds(-20) }).ToList();
        Assert.Empty(Run(G(), early).Findings.Where(f => f.Kind == AnalysisKind.FrameDrop && f.Seconds > 20));
    }

    [Fact]
    public void Fast_temperature_rise_with_a_load_jump_is_a_heavy_scene()
    {
        var a = Run(G(), Session((t, s) => t is >= 100 and <= 108 ? s with { CpuTempC = 60 + (t - 98) * 2.5, CpuLoad = 90, GpuLoad = 90 } : t > 108 ? s with { CpuTempC = 80, CpuLoad = 90 } : s));
        var f = Assert.Single(a.Findings, x => x.Kind == AnalysisKind.TempSpike);
        Assert.Equal(AnalysisCause.HeavyScene, f.Cause);
        Assert.Contains("CPU", f.Title);
        Assert.Contains(f.Suggestions, x => x.Fix == StutterFix.ThermalGuardOn);
    }

    [Fact]
    public void Temperature_rise_while_barely_working_is_a_cooling_problem()
    {
        var a = Run(G(), Session((t, s) => t is >= 100 and <= 108 ? s with { GpuTempC = 70 + (t - 98) * 1.5, GpuLoad = 30, CpuLoad = 20 } : s));
        Assert.Equal(AnalysisCause.CoolingProblem, Assert.Single(a.Findings, x => x.Kind == AnalysisKind.TempSpike).Cause);
    }

    [Fact]
    public void Slow_warming_is_not_a_spike()
    {
        var a = Run(G(), Session((t, s) => s with { CpuTempC = 55 + t / 20.0 }));
        Assert.DoesNotContain(a.Findings, f => f.Kind == AnalysisKind.TempSpike);
    }

    [Fact]
    public void Live_notes_are_given_once_per_cause()
    {
        var a = Run(G(), Session((t, s) => t is >= 100 and < 130 ? s with { Fps = 40, GpuLoad = 99, CpuLoad = 55 } : s));
        var shown = new HashSet<(AnalysisKind, AnalysisCause)>();
        Assert.NotNull(SessionAnalyst.NextLiveNote(a, shown));
        Assert.Null(SessionAnalyst.NextLiveNote(a, shown));
    }

    [Fact]
    public void History_keeps_five_sessions_and_counts_recurring_causes()
    {
        var h = new GameAnalysisHistory();
        for (var i = 0; i < 7; i++)
        {
            var a = new SessionAnalysis { SessionStart = T0.AddDays(i) };
            if (i % 2 == 0) a.Findings.Add(new(AnalysisKind.FrameDrop, AnalysisCause.GpuBound, 1, 10, "t", "d", "e", []));
            h.Add(a);
        }
        Assert.Equal(5, h.Sessions.Count);
        Assert.Equal(3, h.SessionsWith(AnalysisKind.FrameDrop, AnalysisCause.GpuBound)); // days 6, 4, 2 of the newest five (6,5,4,3,2)
    }

    [Fact]
    public void Lower_fps_target_steps_down_and_uncapped_games_get_60()
    {
        var g = G(110);
        Assert.NotNull(StutterFixes.Apply(g, new LauncherSettings(), StutterFix.LowerFpsTarget));
        Assert.Equal(100, g.FpsTarget);
        var u = G(null);
        StutterFixes.Apply(u, new LauncherSettings(), StutterFix.LowerFpsTarget);
        Assert.Equal(60, u.FpsTarget);
        Assert.Null(StutterFixes.Apply(G(30), new LauncherSettings(), StutterFix.LowerFpsTarget));
    }
}
