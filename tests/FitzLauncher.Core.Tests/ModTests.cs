using System.IO.Compression;
using System.Text;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Trainers;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class ModPlannerTests
{
    private static GameModLayout Beth() => new() { Framework = ModFramework.Bethesda, DataDir = "Data", Loaders = new(StringComparer.OrdinalIgnoreCase) { "SKSE" } };
    private static bool None(string _) => false;

    [Fact]
    public void Bethesda_loose_plugin_and_meshes_go_to_Data_after_stripping_wrapper()
    {
        var plan = ModInstallPlanner.Plan(["Cool Armor 1.2/CoolArmor.esp", "Cool Armor 1.2/meshes/armor/a.nif", "Cool Armor 1.2/readme.txt"], Beth(), "Cool Armor", None);
        Assert.Equal("Data", plan.TargetRoot);
        Assert.Contains(plan.Files, f => f.Dest == "Data/CoolArmor.esp");
        Assert.Contains(plan.Files, f => f.Dest == "Data/meshes/armor/a.nif");
        Assert.Equal(["CoolArmor.esp"], plan.Plugins);
    }

    [Fact]
    public void SKSE_plugin_requires_SKSE()
    {
        var plan = ModInstallPlanner.Plan(["SKSE/Plugins/Fix.dll"], Beth(), "Fix", None);
        Assert.Equal("Data/SKSE/Plugins/Fix.dll", plan.Files.Single().Dest);
        Assert.Contains("SKSE", plan.Requires);
    }

    [Fact]
    public void Archive_mirroring_game_folders_installs_at_root()
    {
        var plan = ModInstallPlanner.Plan(["bin/x64/thing.dll", "r6/scripts/a.reds"], new GameModLayout { Framework = ModFramework.Cyberpunk }, "x", None);
        Assert.Equal("", plan.TargetRoot);
        Assert.Contains(plan.Files, f => f.Dest == "r6/scripts/a.reds");
        Assert.Contains("redscript", plan.Requires);
    }

    [Fact]
    public void Unreal_pak_files_go_flat_into_mods_folder()
    {
        var layout = new GameModLayout { Framework = ModFramework.Unreal, PaksModsDir = "Game/Content/Paks/~mods" };
        var plan = ModInstallPlanner.Plan(["My Mod/sub/MyMod_P.pak"], layout, "My Mod", None);
        Assert.Equal("Game/Content/Paks/~mods/MyMod_P.pak", plan.Files.Single().Dest);
    }

    [Fact]
    public void Loose_dll_for_BepInEx_game_goes_into_its_own_plugins_folder()
    {
        var layout = new GameModLayout { Framework = ModFramework.Unity, Loaders = new(StringComparer.OrdinalIgnoreCase) { "BepInEx" } };
        var plan = ModInstallPlanner.Plan(["CoolPlugin.dll"], layout, "Cool Plugin", None);
        Assert.Equal("BepInEx/plugins/Cool Plugin/CoolPlugin.dll", plan.Files.Single().Dest);
        Assert.Contains("BepInEx", plan.Requires);
    }

    [Fact]
    public void Thunderstore_package_uses_r2modman_layout_and_pack_installs_loader()
    {
        var layout = new GameModLayout { Framework = ModFramework.Unity };
        var plan = ModInstallPlanner.Plan(["manifest.json", "icon.png", "README.md", "plugins/A.dll", "config/a.cfg"], layout, "Owner-Mod", None);
        Assert.Contains(plan.Files, f => f.Dest == "BepInEx/plugins/Owner-Mod/A.dll");
        Assert.Contains(plan.Files, f => f.Dest == "BepInEx/config/a.cfg");
        Assert.DoesNotContain(plan.Files, f => f.Dest.EndsWith("manifest.json"));

        var pack = ModInstallPlanner.Plan(["manifest.json", "icon.png", "BepInExPack/winhttp.dll", "BepInExPack/BepInEx/core/BepInEx.dll"], layout, "BepInEx-BepInExPack", None);
        Assert.Contains(pack.Files, f => f.Dest == "winhttp.dll");
        Assert.Contains(pack.Files, f => f.Dest == "BepInEx/core/BepInEx.dll");
        Assert.DoesNotContain("BepInEx", pack.Requires);
    }

    [Fact]
    public void Path_traversal_entries_are_dropped()
    {
        var plan = ModInstallPlanner.Plan(["Data/ok.esp", "../../evil.dll"], Beth(), "x", None, overrideRoot: "");
        Assert.DoesNotContain(plan.Files, f => f.Dest.Contains(".."));
        Assert.Contains(plan.Notes, n => n.Contains("outside"));
    }

    [Fact]
    public void Fomod_picks_required_and_recommended_options()
    {
        const string xml = """
            <config>
              <moduleName>Test</moduleName>
              <requiredInstallFiles><folder source="00 Core" destination="" /></requiredInstallFiles>
              <installSteps><installStep name="Main"><optionalFileGroups>
                <group name="Res" type="SelectExactlyOne"><plugins>
                  <plugin name="1K"><files><folder source="01 1K" destination="textures" /></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
                  <plugin name="2K"><files><folder source="02 2K" destination="textures" /></files><typeDescriptor><type name="Recommended"/></typeDescriptor></plugin>
                </plugins></group>
              </optionalFileGroups></installStep></installSteps>
            </config>
            """;
        var entries = new[] { "Mod/fomod/ModuleConfig.xml", "Mod/00 Core/Test.esp", "Mod/01 1K/a.dds", "Mod/02 2K/a.dds" };
        var plan = ModInstallPlanner.Plan(entries, Beth(), "Test", None, fomodXml: xml);
        Assert.Contains(plan.Files, f => f.Entry == "Mod/02 2K/a.dds" && f.Dest == "Data/textures/a.dds");
        Assert.DoesNotContain(plan.Files, f => f.Entry == "Mod/01 1K/a.dds");
        Assert.Contains(plan.Files, f => f.Dest == "Data/Test.esp");
        Assert.Contains("2K", plan.Notes[0]);
    }
}

public class ModDeployerTests
{
    private static InstalledMod Mod(string name, int prio, params string[] files) => new() { Name = name, Priority = prio, Files = [.. files] };

    private static void Stage(TestDir t, ModDeployer d, InstalledMod m, string content)
    {
        foreach (var f in m.Files) t.File(Path.GetRelativePath(t.Root, Path.Combine(d.StagingFor(m), f)), content);
    }

    [Fact]
    public void Enable_disable_restores_originals_and_respects_priority()
    {
        using var t = new TestDir();
        var game = t.Dir("game");
        t.File("game/Data/shared.txt", "original");
        var d = new ModDeployer(game, t.Path_("staging"), t.Path_("originals"));
        var a = Mod("A", 0, "Data/shared.txt", "Data/a.txt");
        var b = Mod("B", 1, "Data/shared.txt");
        Stage(t, d, a, "from A");
        Stage(t, d, b, "from B");
        var all = new List<InstalledMod> { a, b };
        b.Enabled = false; // B is turned on second

        d.Deploy(a, all);
        Assert.Equal("from A", File.ReadAllText(Path.Combine(game, "Data", "shared.txt")));
        b.Enabled = true;
        d.Deploy(b, all);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(game, "Data", "shared.txt")));

        d.Undeploy(b, all); b.Enabled = false;
        Assert.Equal("from A", File.ReadAllText(Path.Combine(game, "Data", "shared.txt")));

        d.Undeploy(a, all); a.Enabled = false;
        Assert.Equal("original", File.ReadAllText(Path.Combine(game, "Data", "shared.txt")));
        Assert.False(File.Exists(Path.Combine(game, "Data", "a.txt")));
    }

    [Fact]
    public void Conflicts_name_the_winner()
    {
        var a = Mod("A", 0, "x");
        var b = Mod("B", 5, "x");
        var c = ModDeployer.Conflicts([a, b]).Single();
        Assert.Equal("B", c.Winner.Name);
    }

    [Fact]
    public void Plugins_txt_add_and_remove()
    {
        using var t = new TestDir();
        var p = t.File("Plugins.txt", "*Skyrim.esm\n*Other.esp\n");
        PluginsTxt.Add(p, ["New.esp", "Skyrim.esm"], starFormat: true);
        Assert.Contains("*New.esp", File.ReadAllLines(p));
        PluginsTxt.Remove(p, ["New.esp"]);
        Assert.DoesNotContain("*New.esp", File.ReadAllLines(p));
        Assert.Contains("*Other.esp", File.ReadAllLines(p));
    }

    [Fact]
    public void Reads_plugin_masters()
    {
        using var t = new TestDir();
        var path = t.Path_("Test.esp");
        File.WriteAllBytes(path, Esp("Skyrim.esm", "Needed.esm"));
        Assert.Equal(["Skyrim.esm", "Needed.esm"], PluginMasters.Read(path));
    }

    internal static byte[] Esp(params string[] masters)
    {
        var sub = new MemoryStream();
        var w = new BinaryWriter(sub);
        w.Write("HEDR"u8.ToArray()); w.Write((ushort)12); w.Write(new byte[12]);
        foreach (var m in masters)
        {
            var name = Encoding.Latin1.GetBytes(m + "\0");
            w.Write("MAST"u8.ToArray()); w.Write((ushort)name.Length); w.Write(name);
            w.Write("DATA"u8.ToArray()); w.Write((ushort)8); w.Write(new byte[8]);
        }
        var body = sub.ToArray();
        var file = new MemoryStream();
        var fw = new BinaryWriter(file);
        fw.Write("TES4"u8.ToArray()); fw.Write((uint)body.Length); fw.Write(new byte[16]); fw.Write(body);
        return file.ToArray();
    }
}

public class ModDoctorTests
{
    [Fact]
    public void Flags_missing_loader_master_and_extender_mismatch()
    {
        using var t = new TestDir();
        var root = t.Dir("game");
        t.File("game/Data/Skyrim.esm");
        var mod = new InstalledMod { Name = "Armor", Files = ["Data/Armor.esp"], Requires = ["SKSE"] };
        var game = new Game { Name = "Skyrim", ExePath = Path.Combine(root, "SkyrimSE.exe") };
        game.ModSetup.Mods.Add(mod);
        var staged = Path.Combine(t.Root, "stage");
        Directory.CreateDirectory(Path.Combine(staged, "Data"));
        File.WriteAllBytes(Path.Combine(staged, "Data", "Armor.esp"), ModDeployerTests.Esp("Skyrim.esm", "Missing.esm"));
        var layout = new GameModLayout { Framework = ModFramework.Bethesda, DataDir = "Data", ScriptExtenderName = "SKSE", ScriptExtenderRuntime = "1.6.640" };

        var issues = ModCompatibility.Check(new ModCheckContext
        {
            Game = game, Layout = layout, GameRoot = root, GameVersion = "1.6.1170.0",
            StagedPath = (_, rel) => Path.Combine(staged, rel.Replace('/', Path.DirectorySeparatorChar)),
        });
        Assert.Contains(issues, i => i.Title.Contains("needs SKSE"));
        Assert.Contains(issues, i => i.Title.Contains("missing masters") && i.Detail.Contains("Missing.esm"));
        Assert.Contains(issues, i => i.Title.Contains("doesn't match the game version"));
    }

    [Fact]
    public void Troubleshooter_blames_mod_that_owns_crashing_dll_and_bepinex_errors()
    {
        var bad = new InstalledMod { Name = "Better HUD", Files = ["BepInEx/plugins/BetterHUD/BetterHUD.dll"] };
        var ok = new InstalledMod { Name = "Other", Files = ["BepInEx/plugins/Other/Other.dll"] };
        var game = new Game { Name = "G", ExePath = "G.exe", LastCrash = new CrashInfo { FaultingModule = "BetterHUD.dll" } };
        game.ModSetup.Mods.AddRange([bad, ok]);
        var log = new LogFile("BepInEx/LogOutput.log", "[Info   :   BepInEx] Loading\n[Error  : BetterHUD] NullReferenceException\n", DateTime.UtcNow);
        var report = ModTroubleshooter.Analyze(game, new GameModLayout(), [log]);
        Assert.Equal(bad.Id, report.Suspects[0]);
        Assert.Contains("Better HUD", report.Summary);
    }

    [Fact]
    public void Exit_code_is_explained()
    {
        var game = new Game { Name = "G", ExePath = "G.exe", LastCrash = new CrashInfo { FaultingModule = "exit code 0xC0000135" } };
        var report = ModTroubleshooter.Analyze(game, new GameModLayout(), []);
        Assert.Contains(report.Findings, f => f.Title.Contains("DLL is missing"));
    }

    [Fact]
    public void Bisect_finds_the_culprit()
    {
        var setup = new ModSetup();
        for (var i = 0; i < 8; i++) setup.Mods.Add(new InstalledMod { Name = $"M{i}" });
        var culprit = setup.Mods[5].Id;
        var step = ModBisect.Start(setup);
        var rounds = 0;
        while (!step.Done && rounds++ < 10) step = ModBisect.Report(setup, crashed: step.EnableSet.Contains(culprit));
        Assert.Equal(culprit, step.Culprit);
        Assert.DoesNotContain(culprit, step.EnableSet);
        Assert.Equal(7, step.EnableSet.Count);
        Assert.True(rounds <= 4);
    }

    [Fact]
    public void Error_report_hides_user_name()
    {
        Assert.Equal(@"C:\Users\<user>\Documents", ModErrorReport.Redact(@"C:\Users\Fitz\Documents"));
    }
}

public class ModArchiveTests
{
    [Fact]
    public void Extracts_planned_files_from_zip_into_staging()
    {
        using var t = new TestDir();
        var zip = t.Path_("mod.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("Wrap/Data/a.esp").Open())) w.Write("esp");
            using (var w = new StreamWriter(z.CreateEntry("Wrap/readme.txt").Open())) w.Write("hi");
        }
        var entries = ModArchive.ListEntries(zip);
        var plan = ModInstallPlanner.Plan(entries, new GameModLayout { DataDir = "Data", Framework = ModFramework.Bethesda }, "x", _ => false);
        var written = ModArchive.Extract(zip, plan.Files, t.Path_("stage"));
        Assert.Contains("Data/a.esp", written);
        Assert.Equal("esp", File.ReadAllText(Path.Combine(t.Root, "stage", "Data", "a.esp")));
    }
}

public class FlingParserTests
{
    [Fact]
    public void Parses_search_page_and_trainer_page()
    {
        const string search = """
            <h2><a href="https://flingtrainer.com/trainer/elden-ring-trainer/">Elden Ring Trainer</a></h2>
            <a href="https://flingtrainer.com/category/trainer/">Trainers</a>
            """;
        var results = FlingParser.ParseSearch(search);
        Assert.Single(results);
        Assert.Equal("Elden Ring Trainer", results[0].Title);

        const string page = """
            <title>Elden Ring Trainer | FLiNG Trainer</title>
            <p>Num 1 – Infinite Health<br>Num 2 &#8211; Infinite Stamina<br>Ctrl+Num 1 - Edit Runes</p>
            <a href="https://flingtrainer.com/downloads/elden-ring-v1-02-v1-10-plus-24-trainer.zip">Elden Ring v1.02-v1.10 Plus 24 Trainer</a>
            <a href="https://evil.example.com/x.zip">Mirror</a>
            """;
        var p = FlingParser.ParsePage(page, "https://flingtrainer.com/trainer/elden-ring-trainer/");
        Assert.Equal("Elden Ring Trainer", p.Title);
        Assert.Single(p.Downloads);
        Assert.Equal("v1.02-v1.10", p.Downloads[0].Versions);
        Assert.Contains(p.Cheats, c => c.Hotkey == "Num 1" && c.Name == "Infinite Health");
        Assert.Contains(p.Cheats, c => c.Hotkey == "Num 2" && c.Name == "Infinite Stamina");
        Assert.Contains(p.Cheats, c => c.Hotkey.StartsWith("Ctrl") && c.Name == "Edit Runes");
    }

    [Theory]
    [InlineData("v1.02-v1.10", "1.5.0.0", VersionMatch.Likely)]
    [InlineData("v1.02-v1.10", "1.12", VersionMatch.Mismatch)]
    [InlineData("v1.0+", "2.3", VersionMatch.Likely)]
    [InlineData("v1.2", "1.2.0.12345", VersionMatch.Likely)]
    [InlineData(null, "1.0", VersionMatch.Unknown)]
    public void Checks_version_range(string? range, string game, VersionMatch expected) =>
        Assert.Equal(expected, FlingParser.Check(range, game));

    [Fact]
    public void Reads_repack_version_from_folder_name() =>
        Assert.Equal("1.2.3", FlingParser.VersionFromName("Cool Game v1.2.3 [FitGirl Repack]"));
}

public class ArtPaletteTests
{
    private static byte[] Fill(int n, byte r, byte g, byte b, int greyEvery = 0)
    {
        var px = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            var grey = greyEvery > 0 && i % greyEvery != 0;
            px[i * 4] = grey ? (byte)40 : b; px[i * 4 + 1] = grey ? (byte)40 : g; px[i * 4 + 2] = grey ? (byte)40 : r; px[i * 4 + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void Picks_the_dominant_saturated_hue_and_keeps_play_button_readable()
    {
        var c = FitzLauncher.Core.Artwork.ArtPalette.From(Fill(48 * 64, 200, 40, 40, greyEvery: 3), 48, 64);
        Assert.True(c.FromArt);
        var (h, _, _) = FitzLauncher.Core.Artwork.ArtPalette.ToHsl(c.Accent.R, c.Accent.G, c.Accent.B);
        Assert.True(h < 15 || h > 345, $"hue {h}");
        Assert.True(FitzLauncher.Core.Artwork.Rgb.Contrast(c.Accent, new(0x04, 0x12, 0x1C)) >= 4.5);
        Assert.True(c.Deep.Luminance < 0.01);
    }

    [Fact]
    public void Grey_art_falls_back_to_default()
    {
        var c = FitzLauncher.Core.Artwork.ArtPalette.From(Fill(1000, 90, 90, 90), 25, 40);
        Assert.False(c.FromArt);
    }

    [Fact]
    public void Dark_blue_art_is_lifted_to_readable_accent()
    {
        var c = FitzLauncher.Core.Artwork.ArtPalette.From(Fill(1000, 30, 50, 160), 25, 40);
        Assert.True(FitzLauncher.Core.Artwork.Rgb.Contrast(c.Accent, new(0x04, 0x12, 0x1C)) >= 4.5);
    }
}

public class ControllerMathTests
{
    private static readonly FitzLauncher.Core.Models.StickSettings Def = new() { InnerDeadzone = 10, OuterDeadzone = 5 };

    [Fact]
    public void Inside_deadzone_is_zero_and_edge_reaches_full()
    {
        Assert.Equal((0, 0), FitzLauncher.Core.Input.ControllerMath.Stick(2000, 1000, Def));
        var (x, _) = FitzLauncher.Core.Input.ControllerMath.Stick(32767 * 96 / 100, 0, Def);
        Assert.Equal(32767, x);
    }

    [Fact]
    public void Direction_is_kept_and_invert_flips_y()
    {
        var s = new FitzLauncher.Core.Models.StickSettings { InnerDeadzone = 0, OuterDeadzone = 0, InvertY = true };
        var (x, y) = FitzLauncher.Core.Input.ControllerMath.Stick(10000, 10000, s);
        Assert.Equal(x, -y);
        Assert.True(x > 0);
    }

    [Fact]
    public void Anti_deadzone_lifts_small_movements()
    {
        var s = new FitzLauncher.Core.Models.StickSettings { InnerDeadzone = 10, OuterDeadzone = 0, AntiDeadzone = 20 };
        var (x, _) = FitzLauncher.Core.Input.ControllerMath.Stick(32767 * 11 / 100, 0, s);
        Assert.True(x >= 32767 * 0.2, $"{x}");
    }

    [Fact]
    public void Precise_curve_is_lower_than_linear_mid_travel()
    {
        var lin = FitzLauncher.Core.Input.ControllerMath.Stick(16000, 0, new() { InnerDeadzone = 0, OuterDeadzone = 0 });
        var pre = FitzLauncher.Core.Input.ControllerMath.Stick(16000, 0, new() { InnerDeadzone = 0, OuterDeadzone = 0, Curve = FitzLauncher.Core.Models.StickCurve.Precise });
        Assert.True(pre.x < lin.x);
    }

    [Fact]
    public void Triggers_rumble_and_swap()
    {
        Assert.Equal(0, FitzLauncher.Core.Input.ControllerMath.Trigger(8, 5, 100));
        Assert.Equal(255, FitzLauncher.Core.Input.ControllerMath.Trigger(130, 0, 50));
        Assert.Equal(0, FitzLauncher.Core.Input.ControllerMath.Rumble(200, 0));
        Assert.Equal(65535, FitzLauncher.Core.Input.ControllerMath.Rumble(255, 150));
        Assert.Equal(FitzLauncher.Core.Input.ControllerMath.B, FitzLauncher.Core.Input.ControllerMath.Buttons(FitzLauncher.Core.Input.ControllerMath.A, swapFace: true));
    }
}

public class GameUpdateTests
{
    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("v1.2.0", "1.2", 0)]
    [InlineData("1.0.3", "1.0.12", -1)]
    public void Compares_versions_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(FitzLauncher.Core.GameUpdates.GameUpdateLogic.Compare(a, b)!.Value));

    [Theory]
    [InlineData("Patch 1.6.2 is now live", "1.6.2")]
    [InlineData("Hotfix v1.04 notes", "1.04")]
    [InlineData("Update 2.1: new region", "2.1")]
    [InlineData("Version 3.0.1a released", "3.0.1a")]
    [InlineData("Winter sale starts 12.12", null)]
    public void Reads_version_from_patch_titles(string title, string? expected) =>
        Assert.Equal(expected, FitzLauncher.Core.GameUpdates.GameUpdateLogic.VersionFromTitle(title));

    [Fact]
    public void Decides_by_version_then_by_date()
    {
        Assert.Equal(FitzLauncher.Core.Models.UpdateState.Available, FitzLauncher.Core.GameUpdates.GameUpdateLogic.Decide("1.2", "1.3", null, null).state);
        Assert.Equal(FitzLauncher.Core.Models.UpdateState.UpToDate, FitzLauncher.Core.GameUpdates.GameUpdateLogic.Decide("1.3", "1.3", null, null).state);
        var now = DateTimeOffset.Now;
        Assert.Equal(FitzLauncher.Core.Models.UpdateState.ProbablyAvailable, FitzLauncher.Core.GameUpdates.GameUpdateLogic.Decide(null, null, now, now.AddDays(-30)).state);
        Assert.Equal(FitzLauncher.Core.Models.UpdateState.CantCheck, FitzLauncher.Core.GameUpdates.GameUpdateLogic.Decide(null, null, null, null).state);
    }

    [Fact]
    public void Plans_archive_by_matching_existing_files_and_rolls_back()
    {
        using var t = new TestDir();
        var game = t.Dir("game");
        t.File("game/Game.exe", "old exe");
        t.File("game/Data/a.pak", "old pak");
        var zip = t.Path_("patch.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("Game v1.3 Update/Game.exe").Open())) w.Write("new exe");
            using (var w = new StreamWriter(z.CreateEntry("Game v1.3 Update/Data/b.pak").Open())) w.Write("new pak");
        }
        var entries = FitzLauncher.Core.Mods.ModArchive.ListEntries(zip);
        var plan = FitzLauncher.Core.GameUpdates.GameUpdater.Plan(zip, entries, rel => File.Exists(Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(FitzLauncher.Core.GameUpdates.UpdatePackageKind.Files, plan.Kind);
        Assert.Equal(1, plan.MatchedExisting);
        Assert.Contains(plan.Files, f => f.Dest == "Game.exe");

        var u = new FitzLauncher.Core.GameUpdates.GameUpdater(game, t.Path_("backup"));
        u.BackupFor(plan);
        u.ApplyFiles(zip, plan);
        Assert.Empty(u.Verify(plan));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(game, "Game.exe")));

        Assert.Empty(u.Rollback());
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(game, "Game.exe")));
        Assert.False(File.Exists(Path.Combine(game, "Data", "b.pak")));
        Assert.True(File.Exists(Path.Combine(game, "Data", "a.pak")));
    }

    [Fact]
    public void Installer_in_archive_is_detected()
    {
        var plan = FitzLauncher.Core.GameUpdates.GameUpdater.Plan("u.zip", ["Update/setup.exe", "Update/setup-1.bin"], _ => false);
        Assert.Equal(FitzLauncher.Core.GameUpdates.UpdatePackageKind.InstallerInArchive, plan.Kind);
        Assert.Equal("Update/setup.exe", plan.InstallerEntry);
    }
}

public class GameUpdateRollbackTests
{
    [Fact]
    public void Full_restore_keeps_saves_inside_the_game_folder()
    {
        using var t = new TestDir();
        var game = t.Dir("game");
        t.File("game/Game.exe", "v1");
        t.File("game/saves/slot1.sav", "old progress");
        var u = new FitzLauncher.Core.GameUpdates.GameUpdater(game, t.Path_("backup"));
        u.FullBackup();

        // The update changed the exe and added a file; the player saved afterwards.
        File.WriteAllText(Path.Combine(game, "Game.exe"), "v2");
        File.WriteAllText(Path.Combine(game, "new.dll"), "x");
        File.WriteAllText(Path.Combine(game, "saves", "slot1.sav"), "new progress");
        File.WriteAllText(Path.Combine(game, "saves", "slot2.sav"), "brand new save");

        Assert.Empty(u.Rollback(rel => rel.StartsWith("saves/")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(game, "Game.exe")));
        Assert.False(File.Exists(Path.Combine(game, "new.dll")));
        Assert.Equal("new progress", File.ReadAllText(Path.Combine(game, "saves", "slot1.sav")));
        Assert.True(File.Exists(Path.Combine(game, "saves", "slot2.sav")));
    }
}

public class ErrorExplainerTests
{
    [Fact]
    public void Explains_common_failures_in_plain_english_with_fixes()
    {
        var dll = FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(new DllNotFoundException("xinput1_4.dll"));
        Assert.Equal("A system component is missing", dll.Title);
        Assert.Contains(dll.Fixes, f => f.Url.Contains("vc_redist"));

        var full = FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(new IOException("disk full", unchecked((int)0x80070070)));
        Assert.Equal("The disk is full", full.Title);

        var wrapped = FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(new AggregateException(new MissingMethodException("x")));
        Assert.Equal("Launcher files don't match", wrapped.Title);

        var unknown = FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(new InvalidOperationException("boom"));
        Assert.Contains(unknown.Fixes, f => f.Url.Contains("releases"));
    }

    [Fact]
    public void Report_hides_user_name_and_issue_link_is_bounded()
    {
        Exception ex;
        try { throw new InvalidOperationException(@"C:\Users\Fitz\AppData\x"); } catch (Exception e) { ex = e; }
        var report = FitzLauncher.Core.Diagnostics.ErrorExplainer.Report(ex, "0.3.15", "Windows 11", "test");
        Assert.DoesNotContain("Fitz\\AppData", report);
        Assert.Contains("<user>", report);
        var url = FitzLauncher.Core.Diagnostics.ErrorExplainer.IssueUrl("Something went wrong", new string('x', 5000));
        Assert.True(url.Length < 6000);
    }
}

public class CoolingAndCapTests
{
    [Fact]
    public void Cooling_modes_get_progressively_cooler()
    {
        var off = FitzLauncher.Core.Optimization.CoolingPlan.For(FitzLauncher.Core.Optimization.CoolingMode.Off);
        var coolest = FitzLauncher.Core.Optimization.CoolingPlan.For(FitzLauncher.Core.Optimization.CoolingMode.Coolest);
        Assert.Null(off.BoostMode);
        Assert.Equal(0, coolest.BoostMode);
        Assert.True(coolest.PowerScale < 1);
        Assert.Equal(100, FitzLauncher.Core.Optimization.CoolingPlan.StepDown(110, 1));
        Assert.Equal(60, FitzLauncher.Core.Optimization.CoolingPlan.StepDown(90, 1));
        Assert.Equal(60, FitzLauncher.Core.Optimization.CoolingPlan.StepDown(60, 2));
    }

    [Fact]
    public void Caps_are_remembered_per_pc_and_screen()
    {
        var g = new FitzLauncher.Core.Models.Game { CapPerDevice = true };
        var handheld = new FitzLauncher.Core.Models.DisplayInfo { Width = 1920, Height = 1200, MaxRefreshHz = 120 };
        var monitor = new FitzLauncher.Core.Models.DisplayInfo { Width = 3440, Height = 1440, MaxRefreshHz = 144 };
        FitzLauncher.Core.Optimization.DeviceCaps.Save(g, "onexplayer", handheld, 90);
        FitzLauncher.Core.Optimization.DeviceCaps.Save(g, "onexplayer", monitor, 0);
        Assert.Equal(90, FitzLauncher.Core.Optimization.DeviceCaps.For(g, "ONEXPLAYER", handheld));
        Assert.Equal(0, FitzLauncher.Core.Optimization.DeviceCaps.For(g, "onexplayer", monitor));
        Assert.Null(FitzLauncher.Core.Optimization.DeviceCaps.For(g, "desktop", handheld));
        g.CapPerDevice = false;
        Assert.Null(FitzLauncher.Core.Optimization.DeviceCaps.For(g, "onexplayer", handheld));
    }
}

public class LibrarySnapshotTests
{
    [Fact]
    public void Takes_daily_snapshot_and_restores_it()
    {
        using var t = new TestDir();
        var paths = new FitzLauncher.Core.Storage.AppPaths(t.Root);
        File.WriteAllText(paths.Library, "v1");
        File.WriteAllText(paths.Settings, "s1");
        var snap = FitzLauncher.Core.Storage.LibrarySnapshots.TakeIfDue(paths);
        Assert.NotNull(snap);
        Assert.Null(FitzLauncher.Core.Storage.LibrarySnapshots.TakeIfDue(paths)); // once a day

        File.WriteAllText(paths.Library, "v2 broken");
        FitzLauncher.Core.Storage.LibrarySnapshots.Restore(paths, snap!);
        Assert.Equal("v1", File.ReadAllText(paths.Library));
        Assert.Equal("s1", File.ReadAllText(paths.Settings));
        Assert.Equal(2, FitzLauncher.Core.Storage.LibrarySnapshots.List(paths).Count); // the before-restore one too
    }
}

public class FomodChoiceTests
{
    private const string Xml = """
        <config>
          <installSteps><installStep name="Main"><optionalFileGroups>
            <group name="Res" type="SelectExactlyOne"><plugins>
              <plugin name="1K"><description>Small</description><files><folder source="01 1K" destination="textures" /></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
              <plugin name="2K"><files><folder source="02 2K" destination="textures" /></files><typeDescriptor><type name="Recommended"/></typeDescriptor></plugin>
            </plugins></group>
          </optionalFileGroups></installStep></installSteps>
        </config>
        """;

    [Fact]
    public void Describes_options_with_defaults_and_honours_choices()
    {
        var groups = FitzLauncher.Core.Mods.FomodInstaller.Describe(Xml);
        var g = Assert.Single(groups);
        Assert.True(g.SingleChoice);
        Assert.True(g.Options.Single(o => o.Name == "2K").IsDefault);
        Assert.Equal("Small", g.Options.Single(o => o.Name == "1K").Description);

        var entries = new[] { "01 1K/a.dds", "02 2K/a.dds" };
        var picked = FitzLauncher.Core.Mods.FomodInstaller.Resolve(Xml, entries, new Dictionary<string, HashSet<string>> { [g.Key] = ["1K"] });
        Assert.Equal("01 1K/a.dds", Assert.Single(picked.Files).Source);
    }
}

public class StutterDetectiveTests
{
    [Fact]
    public void Blames_background_app_and_thermal_moments()
    {
        var start = DateTimeOffset.UtcNow;
        var game = new FitzLauncher.Core.Models.Game { Name = "G" };
        var stutters = new List<FitzLauncher.Core.Fps.StutterEvent>
        {
            new(start.AddMinutes(10), 60), new(start.AddMinutes(10).AddSeconds(1), 45),   // background spike
            new(start.AddMinutes(20), 80),                                                 // hot
        };
        var samples = new List<FitzLauncher.Core.Fps.ContextSample>
        {
            new(start.AddMinutes(10), 70, 70, 40, 90, 40, "OneDrive", 25, false),
            new(start.AddMinutes(20), 70, 92, 40, 99, 40, null, 0, true),
        };
        var r = FitzLauncher.Core.Fps.StutterDetective.Analyze(game, start, start.AddMinutes(30), stutters, samples, 90, 90);
        Assert.Equal(3, r.TotalStutters);
        Assert.Equal(FitzLauncher.Core.Fps.StutterCause.BackgroundApp, r.Causes[0].Cause);
        Assert.Contains("OneDrive", r.Causes[0].Title);
        Assert.Contains(r.Causes, c => c.Cause == FitzLauncher.Core.Fps.StutterCause.Thermal);
        Assert.Equal(2, r.WorstMoments.Count);
    }

    [Fact]
    public void Smooth_session_says_so()
    {
        var r = FitzLauncher.Core.Fps.StutterDetective.Analyze(new FitzLauncher.Core.Models.Game(), DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), [], [], 90, 90);
        Assert.Contains("smooth", r.Summary);
    }
}

public class AbTestTests
{
    [Fact]
    public void Picks_the_smoother_cooler_setup_and_calls_ties()
    {
        var t = new FitzLauncher.Core.Optimization.AbTest
        {
            A = new() { Label = "Off" }, B = new() { Label = "Cool" },
            ResultA = new() { AverageFps = 110, OnePercentLowFps = 80, MaxGpuTempC = 88, StutterCount = 20 },
        };
        Assert.Equal("B", t.NextName);
        t.ResultB = new() { AverageFps = 108, OnePercentLowFps = 88, MaxGpuTempC = 78, StutterCount = 8 };
        Assert.True(t.Done);
        Assert.StartsWith("B (Cool) is better", t.Verdict());
        Assert.Contains("10°C cooler", t.Verdict());

        t.ResultB = new() { AverageFps = 110, OnePercentLowFps = 80.5, MaxGpuTempC = 88, StutterCount = 20 };
        Assert.StartsWith("Too close to call", t.Verdict());
    }
}

public class UpscalerAdvisorTests
{
    private static FitzLauncher.Core.Models.GpuInfo Amd8060S => new() { Name = "AMD Radeon(TM) 8060S Graphics", Vendor = FitzLauncher.Core.Models.GpuVendor.Amd };

    [Fact]
    public void Dlss_only_game_on_radeon_suggests_optiscaler()
    {
        var scan = new FitzLauncher.Core.Models.GameScanResult { Upscalers = [FitzLauncher.Core.Models.Upscaler.Dlss] };
        var a = FitzLauncher.Core.Upscalers.UpscalerAdvisor.Advise(scan, Amd8060S);
        Assert.True(a.SuggestOptiScaler);
        Assert.Contains("DLSS", a.InGame);
    }

    [Fact]
    public void Prefers_xess_on_radeon_and_dlss_on_rtx()
    {
        var scan = new FitzLauncher.Core.Models.GameScanResult { Upscalers = [FitzLauncher.Core.Models.Upscaler.Dlss, FitzLauncher.Core.Models.Upscaler.Xess, FitzLauncher.Core.Models.Upscaler.Fsr] };
        Assert.Equal("XeSS", FitzLauncher.Core.Upscalers.UpscalerAdvisor.Advise(scan, Amd8060S).Best);
        Assert.False(FitzLauncher.Core.Upscalers.UpscalerAdvisor.Advise(scan, Amd8060S).SuggestOptiScaler);
        var rtx = new FitzLauncher.Core.Models.GpuInfo { Name = "NVIDIA GeForce RTX 4070", Vendor = FitzLauncher.Core.Models.GpuVendor.Nvidia };
        Assert.Equal("DLSS", FitzLauncher.Core.Upscalers.UpscalerAdvisor.Advise(scan, rtx).Best);
    }

    [Fact]
    public void Anti_cheat_blocks_optiscaler()
    {
        var scan = new FitzLauncher.Core.Models.GameScanResult { Upscalers = [FitzLauncher.Core.Models.Upscaler.Dlss] };
        var a = FitzLauncher.Core.Upscalers.UpscalerAdvisor.Advise(scan, Amd8060S, new FitzLauncher.Core.Mods.GameModLayout { HasAntiCheat = true });
        Assert.False(a.SuggestOptiScaler);
    }

    [Fact]
    public void OptiScaler_avoids_existing_proxy_dll()
    {
        using var t = new TestDir();
        t.File("opti/OptiScaler.dll", "x");
        t.File("opti/OptiScaler.ini", "[x]");
        t.File("opti/libxess.dll", "x");
        var tweak = FitzLauncher.Core.Upscalers.UpscalerAdvisor.OptiScalerTweak(t.Path_("opti"), t.Path_("game"), n => n == "dxgi.dll");
        Assert.NotNull(tweak);
        var targets = tweak!.Actions.OfType<FitzLauncher.Core.Models.FileReplaceAction>().Select(a => Path.GetFileName(a.TargetPath)).ToList();
        Assert.Contains("winmm.dll", targets);
        Assert.Contains("OptiScaler.ini", targets);
        Assert.DoesNotContain("dxgi.dll", targets);
    }
}
