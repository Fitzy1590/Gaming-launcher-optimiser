using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Progress;
using FitzLauncher.Core.Saves;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Core.Tests;

public class AchievementTests
{
    private static SaveContext Ctx(TestDir d) => new(d.Path_("Docs"), d.Path_("SG"), d.Path_("Roaming"), d.Path_("Local"), d.Path_("LocalLow"), d.Path_("Public"));

    [Fact]
    public void Reads_goldberg_progress_with_total_from_steam_settings()
    {
        using var d = new TestDir();
        d.File("Roaming/Goldberg SteamEmu Saves/1245620/achievements.json",
            """{"ACH_A":{"earned":true,"earned_time":1},"ACH_B":{"earned":false},"ACH_C":{"earned":true}}""");
        d.File("Game/steam_settings/achievements.json", """[{"name":"ACH_A"},{"name":"ACH_B"},{"name":"ACH_C"},{"name":"ACH_D"}]""");
        var game = new Game { InstallDir = d.Path_("Game"), SteamAppId = 1245620 };

        var p = AchievementReader.Read(game, null, Ctx(d))!;
        Assert.Equal(2, p.Earned);
        Assert.Equal(4, p.Total);
        Assert.Equal(50, p.Percent);
    }

    [Fact]
    public void Reads_codex_style_ini()
    {
        using var d = new TestDir();
        d.File("Public/Steam/CODEX/990080/achievements.ini", "[ACH_ONE]\nAchieved=1\nUnlockTime=1\n[ACH_TWO]\nAchieved=0\n[ACH_THREE]\nAchieved=1\n");
        var game = new Game { InstallDir = d.Path_("Game"), SteamAppId = 990080 };
        var p = AchievementReader.Read(game, null, Ctx(d), knownTotal: 10)!;
        Assert.Equal(2, p.Earned);
        Assert.Equal(20, p.Percent);
    }

    [Fact]
    public void Achievements_do_not_move_main_story_progress()
    {
        var g = new Game { Achievements = new AchievementSnapshot { Earned = 15, Total = 60 } };
        Assert.Null(g.ProgressPercent);
        g.CompletionPercent = 80;
        Assert.Equal(80, g.ProgressPercent);
    }
}

public class ThermalDiagnosisTests
{
    [Fact]
    public void Distinguishes_hard_work_from_poor_cooling()
    {
        var busy = Enumerable.Range(0, 30).Select(i => new ThermalSample(80 + i * 0.3, 97, 70)).ToList();
        Assert.Contains("working flat out", ThermalDiagnosis.Explain("GPU", busy, 85));

        var idleHot = Enumerable.Range(0, 30).Select(_ => new ThermalSample(88, 35, 20)).ToList();
        Assert.Contains("cooling", ThermalDiagnosis.Explain("CPU", idleHot, 90));
    }
}

public class IntegrityTests
{
    [Fact]
    public void Verifies_md5_and_sfv_lists_like_repacks_ship()
    {
        using var d = new TestDir();
        d.File("Repack/fg-01.bin", "hello");
        d.File("Repack/fg-02.bin", "world");
        // md5("hello") = 5d41402abc4b2a76b9719d911017c592 ; second entry deliberately wrong; third missing.
        d.File("Repack/MD5/fitgirl-bins.md5", "5d41402abc4b2a76b9719d911017c592 *fg-01.bin\n00000000000000000000000000000000 *fg-02.bin\nffffffffffffffffffffffffffffffff *fg-03.bin\n");
        var r = IntegrityChecker.VerifyChecksumFile(d.Path_("Repack", "MD5", "fitgirl-bins.md5"));
        Assert.Equal(3, r.Checked);
        Assert.Contains(r.Issues, i => i.RelativePath == "fg-02.bin" && i.Kind == IntegrityIssueKind.Corrupt);
        Assert.Contains(r.Issues, i => i.RelativePath == "fg-03.bin" && i.Kind == IntegrityIssueKind.Missing);

        // CRC32("hello") = 3610A686
        d.File("Repack/check.sfv", "; comment\nfg-01.bin 3610A686\n");
        Assert.True(IntegrityChecker.VerifyChecksumFile(d.Path_("Repack", "check.sfv")).Ok);
        Assert.Equal(2, IntegrityChecker.FindChecksumFiles(d.Path_("Repack")).Count);
    }

    [Fact]
    public void Baseline_catches_missing_and_changed_files_but_ignores_logs()
    {
        using var d = new TestDir();
        var root = d.Dir("Game");
        d.File("Game/game.exe", "exe");
        d.File("Game/bin/steam_api64.dll", "dll");
        d.File("Game/data.pak", "data");
        d.File("Game/output.log", "x");
        var baseline = IntegrityChecker.CreateBaseline(root, hash: true);
        Assert.DoesNotContain("output.log", baseline.Files.Keys);

        File.Delete(Path.Combine(root, "bin", "steam_api64.dll")); // e.g. quarantined by antivirus
        File.WriteAllText(Path.Combine(root, "data.pak"), "dat!");  // same size, different content
        File.WriteAllText(Path.Combine(root, "output.log"), "a much longer log");

        var quick = IntegrityChecker.VerifyBaseline(root, baseline, full: false);
        Assert.Single(quick.Issues);
        var full = IntegrityChecker.VerifyBaseline(root, baseline, full: true);
        Assert.Equal(2, full.Issues.Count);
        Assert.Contains(full.Issues, i => i.Kind == IntegrityIssueKind.Corrupt);
    }
}

public class GameMoverTests
{
    [Fact]
    public void Moves_files_verifies_and_repoints_every_path()
    {
        using var d = new TestDir();
        var src = d.Dir("SD/Games/Hollow");
        d.File("SD/Games/Hollow/hollow.exe", "exe");
        d.File("SD/Games/Hollow/Data/a.bin", new string('x', 5000));
        d.Dir("SD/Games/Hollow/Saves");
        d.File("SD/Games/Hollow/Saves/s1.sav", "s");
        var game = new Game
        {
            ExePath = Path.Combine(src, "hollow.exe"),
            InstallDir = src,
            SaveDirs = [Path.Combine(src, "Saves"), d.Path_("Elsewhere")],
            AppliedChanges = [new AppliedChange { Action = new FileReplaceAction { TargetPath = Path.Combine(src, "libxess.dll") } }],
        };
        var r = GameMover.Move(game, d.Dir("NVMe/Games"), freeBytes: 10_000_000_000, deleteSource: true);
        Assert.True(r.Ok, r.Message);
        var dest = d.Path_("NVMe", "Games", "Hollow");
        Assert.Equal(Path.Combine(dest, "hollow.exe"), game.ExePath);
        Assert.Equal(Path.Combine(dest, "Saves"), game.SaveDirs[0]);
        Assert.Equal(d.Path_("Elsewhere"), game.SaveDirs[1]);
        Assert.Equal(Path.Combine(dest, "libxess.dll"), ((FileReplaceAction)game.AppliedChanges[0].Action).TargetPath);
        Assert.Equal(5000, new FileInfo(Path.Combine(dest, "Data", "a.bin")).Length);
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void Refuses_when_not_enough_space()
    {
        using var d = new TestDir();
        var src = d.Dir("G");
        d.File("G/big.bin", new string('x', 1000));
        var r = GameMover.Move(new Game { InstallDir = src, ExePath = Path.Combine(src, "big.bin") }, d.Dir("Dest"), freeBytes: 100, deleteSource: true);
        Assert.False(r.Ok);
        Assert.True(Directory.Exists(src));
    }
}

public class SyncTests
{
    [Fact]
    public void Uploads_and_detects_newer_remote_from_another_pc()
    {
        using var d = new TestDir();
        var pcA = new AppPaths(d.Path_("A")); pcA.EnsureCreated();
        File.WriteAllText(pcA.Library, "{\"Games\":[]}");
        var drive = d.Dir("Drive");
        new LibrarySync(pcA, drive).Upload("PC-A", "0.3.1");

        var pcB = new AppPaths(d.Path_("B")); pcB.EnsureCreated(); // fresh install
        var syncB = new LibrarySync(pcB, drive);
        Assert.True(syncB.RemoteIsNewer("PC-B"));
        syncB.Download();
        Assert.Equal("{\"Games\":[]}", File.ReadAllText(pcB.Library));
        Assert.False(new LibrarySync(pcA, drive).RemoteIsNewer("PC-A")); // own upload
    }
}

public class TunerTests
{
    private static SessionSummary S(double avg, double low, double at, int? watts = null, int? mode = null) => new()
    {
        HasFpsData = true,
        AverageFps = avg,
        OnePercentLowFps = low,
        TimeAtTarget = at,
        TargetFps = 117,
        PowerWatts = watts,
        LimiterMode = mode,
        Start = DateTimeOffset.Now.AddMinutes(-20),
        End = DateTimeOffset.Now,
    };

    [Fact]
    public void Power_steps_down_when_easy_and_up_when_missed()
    {
        var s = new LauncherSettings { PowerMinWatts = 25, PowerMaxWatts = 85, PowerDefaultWatts = 65 };
        var g = new Game { FpsTarget = 117 };
        Assert.Equal(65, PowerTuner.WattsFor(g, s));
        Assert.Contains("lowered to 62 W", PowerTuner.Learn(g, S(117, 116, 0.99, watts: 65), s));
        Assert.Contains("raised to 67 W", PowerTuner.Learn(g, S(100, 80, 0.5, watts: 62), s));
        g.PowerAuto = false;
        Assert.Null(PowerTuner.Learn(g, S(117, 116, 0.99, watts: 67), s));
    }

    [Fact]
    public void Limiter_mode_tries_both_then_keeps_the_smoother()
    {
        var g = new Game();
        Assert.Equal(0, PacingTuner.Next(g));
        PacingTuner.Learn(g, S(117, 90, 0.9, mode: 0));
        Assert.Equal(1, PacingTuner.Next(g));
        Assert.Contains("front-edge sync was smoother", PacingTuner.Learn(g, S(116, 105, 0.95, mode: 1)));
        Assert.Equal(1, PacingTuner.Next(g));
    }
}

public class ModToggleTests
{
    [Fact]
    public void Toggles_reshade_but_never_touches_dxvk_or_unknown_dlls()
    {
        using var d = new TestDir();
        var dir = d.Dir("G");
        d.File("G/dxgi.dll", "rs");
        d.File("G/ReShade.ini", "");
        d.File("G/dinput8.dll", "loader");
        string? Describe(string p) => Path.GetFileName(p).StartsWith("dxgi") ? "ReShade" : Path.GetFileName(p).StartsWith("dinput8") ? "Ultimate ASI Loader" : null;

        var mods = ModToggle.Detect(dir, Describe);
        var reshade = Assert.Single(mods);
        Assert.Equal("ReShade", reshade.Kind);
        Assert.Equal(1, ModToggle.Set(mods, enable: false));
        Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll" + ModToggle.OffSuffix)));
        Assert.False(ModToggle.AnyEnabled(ModToggle.Detect(dir, Describe)));
        ModToggle.Set(ModToggle.Detect(dir, Describe), enable: true);
        Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll")));

        using var d2 = new TestDir();
        d2.File("G/dxgi.dll", "dxvk");
        d2.File("G/dxvk.conf", "");
        d2.File("G/ReShade.ini", "");
        Assert.Empty(ModToggle.Detect(d2.Path_("G")));
    }
}
