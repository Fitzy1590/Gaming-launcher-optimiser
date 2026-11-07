using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Presets;
using FitzLauncher.Core.Runtimes;
using FitzLauncher.Core.Saves;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Shaders;
using FitzLauncher.Core.Stats;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class AutoTunerTests
{
    private static SessionSummary Session(double avg, double low, double atTarget, int minutes = 30) => new()
    {
        HasFpsData = true, AverageFps = avg, OnePercentLowFps = low, TimeAtTarget = atTarget,
        Start = DateTimeOffset.Now.AddMinutes(-minutes), End = DateTimeOffset.Now,
    };

    private static OptimizationPlan Plan(params Tweak[] suggestions)
    {
        var p = new OptimizationPlan { Target = 117 };
        p.Tweaks.AddRange(suggestions);
        return p;
    }

    private static Tweak Gfx(string id, VisualImpact impact) => new()
    {
        Id = id, Title = id, Description = "", Category = TweakCategory.Graphics, VisualImpact = impact,
        Actions = [new IniSetAction { FilePath = "x", Key = id, Value = "1" }],
    };

    [Fact]
    public void Applies_least_visible_change_first_then_keeps_it_when_lows_improve()
    {
        var game = new Game { FpsTarget = 117 };
        var plan = Plan(Gfx("shadows", VisualImpact.Medium), Gfx("motionblur", VisualImpact.Low));

        var first = AutoTuner.Evaluate(game, Session(100, 80, 0.5), plan);
        Assert.Equal(AutoTuneActionKind.Apply, first.Kind);
        Assert.Equal("motionblur", first.ToApply!.Id);

        var second = AutoTuner.Evaluate(game, Session(108, 90, 0.7), plan);
        Assert.Equal(AutoTuneOutcome.Kept, game.AutoTune.Steps[0].Outcome);
        Assert.Equal("shadows", second.ToApply!.Id);
        Assert.Equal(90, game.AutoTune.Steps[1].BaselineLow);
    }

    [Fact]
    public void Reverts_a_change_that_did_not_help_and_judges_the_next_against_the_old_baseline()
    {
        var game = new Game { FpsTarget = 117 };
        var plan = Plan(Gfx("a", VisualImpact.Low), Gfx("b", VisualImpact.Low));
        AutoTuner.Evaluate(game, Session(100, 80, 0.5), plan);

        var d = AutoTuner.Evaluate(game, Session(100, 80.5, 0.5), plan);
        Assert.Equal(AutoTuneActionKind.RevertThenApply, d.Kind);
        Assert.Equal("a", d.ToRevert!.TweakId);
        Assert.Equal(80, game.AutoTune.Steps[1].BaselineLow);
    }

    [Fact]
    public void Stops_when_target_is_held()
    {
        var game = new Game { FpsTarget = 117 };
        var d = AutoTuner.Evaluate(game, Session(117, 112, 0.97), Plan(Gfx("a", VisualImpact.Low)));
        Assert.Equal(AutoTuneActionKind.Done, d.Kind);
        Assert.True(game.AutoTune.TargetReached);
    }

    [Fact]
    public void Ignores_short_sessions_and_experimental_or_manual_items()
    {
        var game = new Game { FpsTarget = 100 };
        Assert.Equal(AutoTuneActionKind.None, AutoTuner.Evaluate(game, Session(80, 60, 0.2, minutes: 2), Plan()).Kind);

        var experimental = Gfx("exp", VisualImpact.None);
        var risky = new Tweak { Id = "exp", Title = "", Description = "", Category = TweakCategory.Stutter, Risk = TweakRisk.Experimental, Actions = experimental.Actions };
        var manual = new Tweak { Id = "dlss", Title = "", Description = "", Category = TweakCategory.Graphics, Actions = [new ManualStepAction { Instructions = "x" }] };
        Assert.Equal(AutoTuneActionKind.None, AutoTuner.Evaluate(game, Session(80, 60, 0.2), Plan(risky, manual)).Kind);
    }
}

public class ThermalGovernorTests
{
    [Fact]
    public void Steps_down_when_hot_and_back_up_after_cooling()
    {
        var o = new ThermalGuardOptions { MarginC = 2, StepDownAfterSeconds = 15, RecoverMarginC = 8, RecoverAfterSeconds = 90 };
        var g = new ThermalGovernor(117, o, cpuLimitC: 90, gpuLimitC: 85);
        var t = new DateTime(2026, 1, 1);

        Assert.Null(g.Update(60, 84, t));               // 84 >= 85-2 → hot, timer starts
        Assert.Null(g.Update(60, 84, t.AddSeconds(10)));
        Assert.Equal(110, g.Update(60, 84, t.AddSeconds(16)));
        Assert.Equal(100, g.Update(60, 84, t.AddSeconds(32)));
        Assert.True(g.IsThrottling);

        Assert.Null(g.Update(60, 75, t.AddSeconds(40)));   // cool (< 77), timer starts
        Assert.Equal(110, g.Update(60, 75, t.AddSeconds(131)));
        Assert.Equal(117, g.Update(60, 70, t.AddSeconds(222)));
        Assert.False(g.IsThrottling);
    }

    [Fact]
    public void Never_goes_below_minimum_cap()
    {
        var g = new ThermalGovernor(90, new ThermalGuardOptions { StepDownAfterSeconds = 1, MinimumCap = 75 }, 90, 85);
        var t = DateTime.Now;
        for (var i = 0; i < 20; i++) g.Update(99, 99, t.AddSeconds(i * 2));
        Assert.Equal(75, g.CurrentCap);
    }
}

public class CpuAffinityPlannerTests
{
    private static List<LogicalProcessor> Ryzen7950X3D()
    {
        // 16 cores / 32 threads: CCD0 (cores 0-7) has 96 MB L3, CCD1 32 MB.
        var list = new List<LogicalProcessor>();
        for (var i = 0; i < 32; i++)
        {
            var core = i / 2;
            var ccd = core < 8 ? 0 : 1;
            list.Add(new LogicalProcessor(i, core, 0, ccd, ccd == 0 ? 96L << 20 : 32L << 20));
        }
        return list;
    }

    private static List<LogicalProcessor> Intel13700K()
    {
        // 8 P-cores with HT (16 threads, class 1) + 8 E-cores (class 0), one L3.
        var list = new List<LogicalProcessor>();
        for (var i = 0; i < 16; i++) list.Add(new LogicalProcessor(i, i / 2, 1, 0, 30L << 20));
        for (var i = 16; i < 24; i++) list.Add(new LogicalProcessor(i, 8 + (i - 16), 0, 0, 30L << 20));
        return list;
    }

    [Fact]
    public void Auto_pins_dual_ccd_x3d_to_the_vcache_ccd()
    {
        var plan = CpuAffinityPlanner.Plan(Ryzen7950X3D(), CorePinning.Auto);
        Assert.Equal(0xFFFFUL, plan!.Mask);
        Assert.Contains("V-Cache", plan.Description);
    }

    [Fact]
    public void Auto_pins_intel_hybrid_to_p_cores()
    {
        var plan = CpuAffinityPlanner.Plan(Intel13700K(), CorePinning.Auto);
        Assert.Equal(0xFFFFUL, plan!.Mask);
        Assert.Contains("P-cores", plan.Description);
    }

    [Fact]
    public void Leaves_symmetric_cpus_and_off_alone()
    {
        var single = Enumerable.Range(0, 16).Select(i => new LogicalProcessor(i, i / 2, 0, 0, 96L << 20)).ToList(); // 7800X3D
        Assert.Null(CpuAffinityPlanner.Plan(single, CorePinning.Auto));
        Assert.Null(CpuAffinityPlanner.Plan(Ryzen7950X3D(), CorePinning.Off));
    }
}

public class ScannerFeatureTests
{
    [Fact]
    public void Finds_markers_across_chunk_boundaries_in_ascii_and_utf16()
    {
        using var d = new TestDir();
        var path = d.File("game.exe");
        var bytes = new byte[4 * 1024 * 1024 + 100];
        var marker = System.Text.Encoding.ASCII.GetBytes("NVIDIA Reflex");
        Array.Copy(marker, 0, bytes, 4 * 1024 * 1024 - 5, marker.Length); // straddles the 4 MB boundary
        var utf16 = System.Text.Encoding.Unicode.GetBytes("AntiLag2");
        Array.Copy(utf16, 0, bytes, 1000, utf16.Length);
        File.WriteAllBytes(path, bytes);

        var hits = BinaryFeatureScanner.Find(path, ["NVIDIA Reflex", "AntiLag2", "Nope"]);
        Assert.Contains("NVIDIA Reflex", hits);
        Assert.Contains("AntiLag2", hits);
        Assert.DoesNotContain("Nope", hits);
    }

    [Fact]
    public void Scan_reads_appid_unity_info_redists_and_upscaler_dlls()
    {
        using var d = new TestDir();
        var exe = d.File("G/Game.exe", "MZ AntiLag2");
        d.File("G/steam_appid.txt", "1245620\n");
        d.File("G/UnityPlayer.dll", "x");
        d.File("G/Game_Data/app.info", "Team Cherry\nHollow Knight\n");
        d.File("G/_CommonRedist/vcredist/2019/VC_redist.x64.exe", "x");
        d.File("G/libxess.dll", "x");
        d.File("G/sl.reflex.dll", "x");

        var scanner = new GameScanner(new ScanContext(d.Root, d.Root, d.Root, d.Root), _ => "1.3.0.0");
        var scan = scanner.Scan(new Game { Name = "Hollow", ExePath = exe });
        Assert.Equal(1245620, scan.SteamAppId);
        Assert.Equal("Team Cherry", scan.UnityCompany);
        Assert.Equal("Hollow Knight", scan.UnityProduct);
        Assert.True(scan.SupportsReflex);
        Assert.True(scan.SupportsAntiLag2);
        Assert.Single(scan.RedistInstallers);
        Assert.Equal(UpscalerDllKind.XeSS, Assert.Single(scan.UpscalerDlls).Kind);
    }
}

public class LatencyPlanTests
{
    private static HardwareProfile Hw(GpuVendor v, bool rtss = true) => new()
    {
        Gpus = [new GpuInfo { Name = v == GpuVendor.Amd ? "AMD Radeon RX 7900 XTX" : "NVIDIA GeForce RTX 4080", Vendor = v, IsDiscrete = true }],
        RtssInstalled = rtss, RtssPath = "/rtss",
    };

    [Theory]
    [InlineData(GpuVendor.Nvidia, true, false, "latency-reflex")]
    [InlineData(GpuVendor.Nvidia, false, false, "latency-nv-llm")]
    [InlineData(GpuVendor.Amd, false, true, "latency-antilag2")]
    [InlineData(GpuVendor.Amd, false, false, "latency-antilag")]
    public void Recommends_the_right_low_latency_tech_per_vendor(GpuVendor vendor, bool reflex, bool antiLag2, string expected)
    {
        var scan = new GameScanResult { SupportsReflex = reflex, SupportsAntiLag2 = antiLag2 };
        var plan = new OptimizationPlanner().Build(new Game { Name = "G", ExePath = "/g/g.exe" }, scan, Hw(vendor), 117);
        Assert.Contains(plan.Tweaks, t => t.Id == expected);
        Assert.Contains(plan.Tweaks, t => t.Id == (vendor == GpuVendor.Amd ? "latency-rtss-mode-amd" : "latency-rtss-mode-nv"));
    }
}

public class RedistCatalogTests
{
    [Theory]
    [InlineData(@"C:\G\_CommonRedist\vcredist\2019\VC_redist.x64.exe", "Microsoft Visual C++ 2015-2019 Redistributable (x64) - 14.29.30133", RedistKind.VcRedist, "2015", "x64", "/install /quiet /norestart")]
    [InlineData(@"C:\G\_CommonRedist\vcredist\2010\vcredist_x86.exe", "Microsoft Visual C++ 2010  x86 Redistributable", RedistKind.VcRedist, "2010", "x86", "/q /norestart")]
    [InlineData(@"C:\G\_CommonRedist\DirectX\Jun2010\DXSETUP.exe", "DirectX", RedistKind.DirectX, null, null, "/silent")]
    [InlineData(@"C:\G\_Redist\oalinst.exe", "OpenAL", RedistKind.OpenAL, null, null, "/s")]
    public void Classifies_common_repack_runtimes(string path, string product, RedistKind kind, string? year, string? arch, string args)
    {
        var r = RedistCatalog.Classify(path, product, "1.0")!;
        Assert.Equal(kind, r.Kind);
        Assert.Equal(year, r.Year);
        if (arch is not null) Assert.Equal(arch, r.Arch);
        Assert.Equal(args, r.SilentArgs);
    }

    [Fact]
    public void Dedupes_same_runtime_keeping_newest()
    {
        var a = RedistCatalog.Classify(@"C:\A\VC_redist.x64.exe", "Microsoft Visual C++ 2015-2019 Redistributable (x64)", "14.20.0.0")!;
        var b = RedistCatalog.Classify(@"C:\B\VC_redist.x64.exe", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.38.0.0")!;
        Assert.Equal("14.38.0.0", Assert.Single(RedistCatalog.Dedupe([a, b])).Version);
    }

    [Fact]
    public void Ignores_directx_cabinet_helpers() => Assert.Null(RedistCatalog.Classify(@"C:\G\DirectX\DSETUP.dll", "DirectX", null));
}

public class UpscalerLibraryTests
{
    [Fact]
    public void Offers_newer_dll_from_another_game_and_swap_is_revertible()
    {
        using var d = new TestDir();
        var oldDll = d.File("GameA/nvngx_dlss.dll", "old");
        var newDll = d.File("GameB/nvngx_dlss.dll", "new");
        var scanA = new GameScanResult { UpscalerDlls = [new() { Path = oldDll, Kind = UpscalerDllKind.DlssSuperResolution, Version = "2.4.0.0" }] };
        var best = UpscalerLibrary.BestVersions([
            .. scanA.UpscalerDlls,
            new UpscalerDllInfo { Path = newDll, Kind = UpscalerDllKind.DlssSuperResolution, Version = "310.2.1.0" },
        ]);

        var tweak = Assert.Single(UpscalerLibrary.Upgrades(scanA, best));
        Assert.False(tweak.PartOfOneClick);
        Assert.Contains("2.4.0.0 → 310.2.1.0", tweak.Title);

        var engine = new TweakEngine(new FakeRegistry(), d.Path_("backups"));
        var game = new Game();
        Assert.True(engine.Apply(game.AppliedChanges, tweak, game).Success);
        Assert.Equal("new", File.ReadAllText(oldDll));
        engine.RevertAll(game.AppliedChanges, game);
        Assert.Equal("old", File.ReadAllText(oldDll));
    }

    [Fact]
    public void No_upgrade_when_already_newest()
    {
        var scan = new GameScanResult { UpscalerDlls = [new() { Path = "/x/libxess.dll", Kind = UpscalerDllKind.XeSS, Version = "1.3.0.0" }] };
        Assert.Empty(UpscalerLibrary.Upgrades(scan, new Dictionary<UpscalerDllKind, UpscalerDllInfo>()));
    }
}

public class SaveBackupTests
{
    [Fact]
    public void Locates_emulator_and_unity_saves_by_appid()
    {
        using var d = new TestDir();
        var ctx = new SaveContext(d.Path_("Docs"), d.Path_("SavedGames"), d.Path_("Roaming"), d.Path_("Local"), d.Path_("LocalLow"), d.Path_("Public"));
        d.File("Roaming/Goldberg SteamEmu Saves/367520/remote/user1.dat", "s");
        d.File("LocalLow/Team Cherry/Hollow Knight/user1.dat", "s");
        d.Dir("Docs/My Games/Hollow Knight"); // empty: ignored
        var game = new Game { Name = "Hollow Knight", ExePath = d.File("Install/hollow_knight.exe", "MZ") };
        var scan = new GameScanResult { SteamAppId = 367520, UnityCompany = "Team Cherry", UnityProduct = "Hollow Knight" };

        var found = SaveLocator.FindCandidates(game, scan, ctx);
        Assert.Contains(d.Path_("Roaming", "Goldberg SteamEmu Saves", "367520"), found);
        Assert.Contains(d.Path_("LocalLow", "Team Cherry", "Hollow Knight"), found);
        Assert.DoesNotContain(d.Path_("Docs", "My Games", "Hollow Knight"), found);
    }

    [Fact]
    public void Backs_up_only_when_changed_rotates_syncs_and_restores()
    {
        using var d = new TestDir();
        var save = d.File("saves/slot1.sav", "v1");
        var game = new Game { Name = "Test: Game", SaveDirs = [d.Path_("saves")] };
        var usb = d.Dir("usb");
        var svc = new SaveBackupService(d.Path_("backups"));

        Assert.True(svc.Backup(game, [usb], keep: 2).Created);
        Assert.False(svc.Backup(game, [usb], keep: 2).Created); // unchanged

        Thread.Sleep(1100); // distinct timestamped names
        File.WriteAllText(save, "v2");
        File.SetLastWriteTimeUtc(save, DateTime.UtcNow.AddSeconds(5));
        Assert.True(svc.Backup(game, [usb], keep: 2).Created);
        Assert.Equal(2, svc.List(game).Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(usb, SaveBackupService.DestinationFolder, SaveBackupService.FolderName(game))).Length);

        var oldest = svc.List(game).Last();
        File.WriteAllText(save, "broken");
        Thread.Sleep(1100);
        var restore = svc.Restore(game, oldest.Path, [], keep: 5);
        Assert.True(restore.Created);
        Assert.Equal("v1", File.ReadAllText(save));
    }

    [Fact]
    public void Usb_destination_is_found_by_marker_on_any_drive_letter()
    {
        using var d = new TestDir();
        var driveE = d.Dir("E");
        var dest = BackupDestinations.AddUsb(Path.Combine(driveE, "Backups"), "My USB", driveRoot: driveE);
        var second = BackupDestinations.AddUsb(Path.Combine(driveE, "Other"), "My USB 2", driveRoot: driveE);
        Assert.Equal(dest.DriveId, second.DriveId);

        // Same stick re-plugged as "F".
        var driveF = d.Path_("F");
        Directory.Move(driveE, driveF);
        var resolved = BackupDestinations.Resolve([dest], [d.Path_("C"), driveF]);
        Assert.Equal(Path.Combine(driveF, "Backups"), Assert.Single(resolved));
        Assert.Empty(BackupDestinations.Resolve([dest], [d.Path_("C")])); // unplugged
    }

    [Fact]
    public void Finds_google_drive_folder()
    {
        using var d = new TestDir();
        var g = d.Dir("G/My Drive");
        Assert.Equal(g, BackupDestinations.FindGoogleDrive([d.Path_("C"), d.Path_("G")], d.Path_("home")));
    }
}

public class MiscFeatureTests
{
    [Fact]
    public void Shader_cache_tool_measures_and_clears()
    {
        using var d = new TestDir();
        d.File("Local/D3DSCache/a.bin", new string('x', 100));
        d.File("Local/NVIDIA/DXCache/b.bin", new string('x', 50));
        var caches = ShaderCacheTool.Locations(d.Path_("Local"), d.Path_("LocalLow"));
        Assert.Equal(150, caches.Sum(c => c.Bytes));
        var (freed, skipped) = ShaderCacheTool.Clear(caches[0]);
        Assert.Equal(100, freed);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void User_presets_match_by_exe_and_become_tweaks()
    {
        using var d = new TestDir();
        var file = d.Path_("presets.json");
        Assert.Empty(PresetCatalog.Load(file)); // writes the example, which is ignored
        File.WriteAllText(file, """[{"Id":"stray","Title":"Stray stutter","ExeNames":["Stray-Win64-Shipping.exe"],"Settings":[{"Key":"r.Streaming.PoolSize","Value":"3072"}]}]""");
        var presets = PresetCatalog.Load(file);

        var game = new Game { Name = "Stray", ExePath = "/g/Stray-Win64-Shipping.exe" };
        var scan = new GameScanResult { ConfigDirectories = ["/cfg"] };
        var t = Assert.Single(PresetCatalog.For(game, scan, presets));
        Assert.Equal("preset-stray", t.Id);
        Assert.Equal("3072", Assert.IsType<IniSetAction>(t.Actions[0]).Value);
        Assert.Empty(PresetCatalog.For(game, new GameScanResult(), presets)); // needs a config dir
    }

    [Fact]
    public void Progress_and_totals()
    {
        var a = new Game { TotalPlayTime = TimeSpan.FromHours(15), MainStoryHours = 30, Status = PlayStatus.Playing };
        var b = new Game { TotalPlayTime = TimeSpan.FromHours(5), Status = PlayStatus.Completed };
        var c = new Game { CompletionPercent = 40 };
        Assert.Equal(50, a.ProgressPercent!.Value, 1);
        Assert.Equal(100, b.ProgressPercent);
        Assert.Equal(40, c.ProgressPercent);

        var now = DateTimeOffset.Now;
        var sessions = new[]
        {
            new SessionSummary { Start = now.AddHours(-3), End = now.AddHours(-1) },
            new SessionSummary { Start = now.AddDays(-20), End = now.AddDays(-20).AddHours(4) },
        };
        var totals = LibraryStats.Compute([a, b, c], sessions, now);
        Assert.Equal(20, totals.TotalHours, 1);
        Assert.Equal(2, totals.WeekHours, 1);
        Assert.Equal(6, totals.MonthHours, 1);
        Assert.Equal(1, totals.Completed);
        Assert.Equal(new[] { a, b, c }, LibraryStats.Sort([c, b, a], LibrarySort.MostPlayed).ToArray());
    }
}
