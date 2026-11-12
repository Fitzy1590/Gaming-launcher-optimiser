using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Tests;

public class SessionHoldTests
{
    private static HoldSnapshot Defaults() => new()
    {
        DefenderReadable = true,
        ScanOnlyIfIdleEnabled = false,
        ScanAvgCPULoadFactor = 50,
        EnableLowCpuPriority = false,
        DisableCatchupFullScan = false,
        DisableCatchupQuickScan = false,
        DefragState = "Ready",
    };

    [Fact]
    public void ParsesSnapshotFromLastJsonLine()
    {
        var s = SessionHoldPlanner.ParseSnapshot("noise\r\n{\"DefenderReadable\":true,\"ScanAvgCPULoadFactor\":50,\"DefragState\":\"Ready\"}\r\n");
        Assert.NotNull(s);
        Assert.True(s!.DefenderReadable);
        Assert.Equal(50, s.ScanAvgCPULoadFactor);
        Assert.Equal("Ready", s.DefragState);
        Assert.Null(SessionHoldPlanner.ParseSnapshot("not json"));
        Assert.Null(SessionHoldPlanner.ParseSnapshot("{broken"));
        Assert.Null(SessionHoldPlanner.ParseSnapshot(null));
    }

    [Fact]
    public void PlansAllFiveDefenderChangesFromDefaults()
    {
        var plan = SessionHoldPlanner.PlanDefender(Defaults());
        Assert.Equal(5, plan.Count);
        Assert.Contains(plan, c => c.Name == SessionHoldPlanner.CpuLoad && c.Previous == "50" && c.Applied == "20");
        Assert.Contains(plan, c => c.Name == SessionHoldPlanner.IdleOnly && c.Previous == "False" && c.Applied == "True");
    }

    [Fact]
    public void SkipsSettingsAlreadyGentleAndNeverTouchesUnreadableDefender()
    {
        var s = Defaults();
        s.ScanOnlyIfIdleEnabled = true;
        s.ScanAvgCPULoadFactor = 10; // gentler than ours already
        var plan = SessionHoldPlanner.PlanDefender(s);
        Assert.DoesNotContain(plan, c => c.Name is SessionHoldPlanner.IdleOnly or SessionHoldPlanner.CpuLoad);
        Assert.Empty(SessionHoldPlanner.PlanDefender(new HoldSnapshot { DefenderReadable = false }));
        s.ScanAvgCPULoadFactor = 0; // 0 = no limit, so we do set one
        Assert.Contains(SessionHoldPlanner.PlanDefender(s), c => c.Name == SessionHoldPlanner.CpuLoad);
    }

    [Fact]
    public void KeepsOnlyChangesThatStuckAndAreStillOurs()
    {
        var plan = SessionHoldPlanner.PlanDefender(Defaults());
        var now = Defaults();
        now.ScanOnlyIfIdleEnabled = true;       // stuck
        now.ScanAvgCPULoadFactor = 20;          // stuck
        now.EnableLowCpuPriority = false;       // blocked (Tamper Protection / policy)
        now.DisableCatchupFullScan = true;      // stuck
        now.DisableCatchupQuickScan = false;    // blocked
        var kept = SessionHoldPlanner.StillApplied(plan, now).Select(c => c.Name).ToList();
        Assert.Equal([SessionHoldPlanner.IdleOnly, SessionHoldPlanner.CpuLoad, SessionHoldPlanner.NoCatchupFull], kept);

        now.ScanAvgCPULoadFactor = 35; // the user changed it during the game: leave it
        Assert.DoesNotContain(SessionHoldPlanner.StillApplied(plan, now), c => c.Name == SessionHoldPlanner.CpuLoad);
    }

    [Fact]
    public void ScriptsOnlyContainAllowListedNamesAndLiteralValues()
    {
        var changes = new List<HoldChange>
        {
            new() { Name = SessionHoldPlanner.IdleOnly, Previous = "False", Applied = "True" },
            new() { Name = SessionHoldPlanner.CpuLoad, Previous = "50", Applied = "20" },
            new() { Name = "DisableRealtimeMonitoring", Previous = "False", Applied = "True" }, // never allowed
            new() { Name = SessionHoldPlanner.LowPriority, Previous = "False", Applied = "$(calc)" }, // not a literal
        };
        var apply = SessionHoldPlanner.BuildDefenderScript(changes, apply: true);
        Assert.Contains("-ScanOnlyIfIdleEnabled $true", apply);
        Assert.Contains("-ScanAvgCPULoadFactor 20", apply);
        Assert.DoesNotContain("Realtime", apply);
        Assert.DoesNotContain("calc", apply);
        var undo = SessionHoldPlanner.BuildDefenderScript(changes, apply: false);
        Assert.Contains("-ScanOnlyIfIdleEnabled $false", undo);
        Assert.Contains("-ScanAvgCPULoadFactor 50", undo);
    }

    [Theory]
    [InlineData("Ready", true, false)]
    [InlineData("Queued", true, false)]
    [InlineData("Running", true, true)]
    [InlineData("Disabled", false, false)]
    [InlineData(null, false, false)]
    public void PlansMaintenanceFromTaskState(string? state, bool disable, bool stop)
    {
        var plan = SessionHoldPlanner.PlanMaintenance(new HoldSnapshot { DefragState = state });
        Assert.Equal(disable, plan.DisableTask);
        Assert.Equal(stop, plan.StopRunningTask);
    }

    [Fact]
    public void ReenablesDefragOnlyIfWeDisabledItAndItIsStillDisabled()
    {
        Assert.True(SessionHoldPlanner.ShouldReenableDefrag(true, new HoldSnapshot { DefragState = "Disabled" }));
        Assert.False(SessionHoldPlanner.ShouldReenableDefrag(true, new HoldSnapshot { DefragState = "Ready" }));   // someone else re-enabled it
        Assert.False(SessionHoldPlanner.ShouldReenableDefrag(false, new HoldSnapshot { DefragState = "Disabled" })); // the user's own setting
        Assert.False(SessionHoldPlanner.ShouldReenableDefrag(true, new HoldSnapshot { DefragState = null }));
    }

    [Fact]
    public void MaintenanceScriptStopsBeforeNothingElseAndNamesTheDefragTask()
    {
        var script = SessionHoldPlanner.BuildMaintenanceScript(new MaintenancePlan(true, true));
        Assert.Contains("Disable-ScheduledTask", script);
        Assert.Contains("Stop-ScheduledTask", script);
        Assert.Contains("ScheduledDefrag", script);
        Assert.DoesNotContain("Stop-Service", script); // services are deliberately left alone
    }
}

public class ApuMemoryTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;

    private static HardwareProfile Apu(string maker = "ASUSTeK COMPUTER INC.") => new()
    {
        SystemManufacturer = maker,
        Gpus = [new GpuInfo { Name = "AMD Radeon Graphics", IsDiscrete = false }],
    };

    private static GraphicsMemorySnapshot Mem(double visibleGb, double reservedGb, double freeGb, bool dxgi = true) => new()
    {
        TotalPhysBytes = (ulong)(visibleGb * Gb),
        AvailPhysBytes = (ulong)(freeGb * Gb),
        DedicatedVideoBytes = (ulong)(reservedGb * Gb),
        SharedSystemBytes = 6 * Gb,
        DxgiOk = dxgi,
    };

    [Fact]
    public void SixteenGbWithFourGbReservedIsFine()
    {
        var r = ApuMemoryDiagnostic.Evaluate(Apu(), Mem(12, 4, 8.5));
        Assert.Equal(MemoryLevel.Ok, r.Level);
        Assert.False(r.ShowBadge);
    }

    [Fact]
    public void SixteenGbWithEightGbReservedIsLowAndSuggestsLess()
    {
        var r = ApuMemoryDiagnostic.Evaluate(Apu(), Mem(8, 8, 4.5));
        Assert.Equal(MemoryLevel.Low, r.Level);
        Assert.True(r.ShowBadge);
        Assert.Equal(4, r.SuggestedReservedGb);
        Assert.Contains("Armoury Crate", r.Advice);
        Assert.Contains("never changes", r.Advice);
    }

    [Fact]
    public void SixGbReservedIsTightNotLow()
    {
        var r = ApuMemoryDiagnostic.Evaluate(Apu(), Mem(10, 6, 7.0));
        Assert.Equal(MemoryLevel.Tight, r.Level);
    }

    [Fact]
    public void FreeMemoryNowCanMakeItTight()
    {
        // Plenty visible, but a lot is already in use.
        var r = ApuMemoryDiagnostic.Evaluate(Apu(), Mem(12, 4, 6.5));
        Assert.Equal(MemoryLevel.Tight, r.Level);
    }

    [Fact]
    public void SmallReservationCantBeFixedByBios()
    {
        var r = ApuMemoryDiagnostic.Evaluate(Apu(), Mem(6, 2, 2.5));
        Assert.Equal(MemoryLevel.Low, r.Level);
        Assert.Null(r.SuggestedReservedGb);
        Assert.Contains("Closing other apps", r.Advice);
    }

    [Fact]
    public void BiggerGameNeedChangesTheVerdictAndLenovoGetsBiosAdvice()
    {
        Assert.Equal(MemoryLevel.Ok, ApuMemoryDiagnostic.Evaluate(Apu("LENOVO"), Mem(12, 4, 8.5), 6).Level);
        var r = ApuMemoryDiagnostic.Evaluate(Apu("LENOVO"), Mem(12, 4, 8.5), 10);
        Assert.NotEqual(MemoryLevel.Ok, r.Level);
    }

    [Fact]
    public void UnknownWhenNotAnApuOrNoDxgi()
    {
        Assert.Equal(MemoryLevel.Unknown, ApuMemoryDiagnostic.Evaluate(null, Mem(8, 8, 4)).Level);
        Assert.Equal(MemoryLevel.Unknown, ApuMemoryDiagnostic.Evaluate(Apu(), Mem(8, 8, 4, dxgi: false)).Level);
        Assert.Equal(MemoryLevel.Unknown, ApuMemoryDiagnostic.Evaluate(new HardwareProfile { Gpus = [new GpuInfo { IsDiscrete = true }] }, Mem(8, 8, 4)).Level);
    }
}
