using System.Text.Json.Nodes;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Tests;

public class ConfigPatcherTests
{
    [Fact]
    public void Ini_edits_only_the_value_and_keeps_everything_else()
    {
        const string ini = "; header\r\n[EmuCore/GS]\r\nupscale_multiplier = 1 ; scale\r\nMaxAnisotropy = 0\r\nLoadTextureReplacements = false\r\n[Other]\r\nupscale_multiplier = 9\r\n";
        var r = ConfigPatcher.Patch(ConfigFormat.Ini, ini, [new(["EmuCore/GS"], "upscale_multiplier", "3"), new(["EmuCore/GS"], "LoadTextureReplacements", "true")]);
        Assert.Equal(2, r.Applied.Count);
        Assert.Contains("upscale_multiplier = 3 ; scale\r\n", r.Text);
        Assert.Contains("LoadTextureReplacements = true\r\n", r.Text);
        Assert.Contains("[Other]\r\nupscale_multiplier = 9", r.Text); // other section untouched
        Assert.StartsWith("; header\r\n", r.Text);
    }

    [Fact]
    public void Missing_keys_and_wrong_kinds_are_skipped_not_invented()
    {
        const string ini = "[Settings]\nInternalResolution = 1\nHiresTextures = True\n";
        var r = ConfigPatcher.Patch(ConfigFormat.Ini, ini, [new(["Settings"], "NoSuchKey", "1"), new(["Settings"], "InternalResolution", "high"), new(["Settings"], "HiresTextures", "false")]);
        Assert.Equal(2, r.Skipped.Count);
        Assert.Contains("HiresTextures = False", r.Text); // keeps the file's True/False style
        Assert.Contains("InternalResolution = 1", r.Text);
        Assert.DoesNotContain("NoSuchKey", r.Text);
    }

    [Fact]
    public void Qt_default_flag_is_cleared_so_the_value_counts()
    {
        const string qt = "[Renderer]\nresolution_factor\\default=true\nresolution_factor=1\n";
        var r = ConfigPatcher.Patch(ConfigFormat.Ini, qt, [new(["Renderer"], "resolution_factor", "4")]);
        Assert.Contains("resolution_factor=4", r.Text);
        Assert.Contains("resolution_factor\\default=false", r.Text);
    }

    [Fact]
    public void Yaml_follows_nesting_and_keeps_comments()
    {
        const string yml = "Core:\n  Resolution Scale: 7\nVideo:\n  Renderer: Vulkan\n  Resolution Scale: 100 # percent\n  Anisotropic Filter Override: 0\n";
        var r = ConfigPatcher.Patch(ConfigFormat.Yaml, yml, [new(["Video"], "Resolution Scale", "200"), new(["Video"], "Anisotropic Filter Override", "16")]);
        Assert.Contains("  Resolution Scale: 200 # percent", r.Text);
        Assert.Contains("Core:\n  Resolution Scale: 7", r.Text);
        Assert.Contains("Anisotropic Filter Override: 16", r.Text);
    }

    [Fact]
    public void Toml_keeps_quotes_and_sections()
    {
        const string toml = "[GPU]\ngpu = \"vulkan\"\ndraw_resolution_scale_x = 1\n[APU]\ndraw_resolution_scale_x = 5\n";
        var r = ConfigPatcher.Patch(ConfigFormat.Toml, toml, [new(["GPU"], "draw_resolution_scale_x", "2"), new(["GPU"], "gpu", "d3d12")]);
        Assert.Contains("draw_resolution_scale_x = 2", r.Text);
        Assert.Contains("gpu = \"d3d12\"", r.Text);
        Assert.Contains("[APU]\ndraw_resolution_scale_x = 5", r.Text);
    }

    [Fact]
    public void Json_changes_known_keys_with_their_types()
    {
        const string json = """{"res_scale":1,"max_anisotropy":-1,"enable_shader_cache":true,"nested":{"x":1}}""";
        var r = ConfigPatcher.Patch(ConfigFormat.Json, json, [new([], "res_scale", "3"), new([], "enable_shader_cache", "false"), new([], "missing", "1"), new(["nested"], "x", "2")]);
        var o = JsonNode.Parse(r.Text)!;
        Assert.Equal(3, (int)o["res_scale"]!);
        Assert.False((bool)o["enable_shader_cache"]!);
        Assert.Equal(2, (int)o["nested"]!["x"]!);
        Assert.Single(r.Skipped);
        Assert.Equal(json, ConfigPatcher.Patch(ConfigFormat.Json, json, [new([], "missing", "1")]).Text); // nothing applied = untouched
    }

    [Fact]
    public void Broken_json_changes_nothing()
    {
        var r = ConfigPatcher.Patch(ConfigFormat.Json, "{ nope", [new([], "a", "1")]);
        Assert.Equal("{ nope", r.Text);
        Assert.False(r.Changed);
    }
}

public class EmuTunerTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(20);

    [Fact]
    public void Ladder_levels_rise_in_quality()
    {
        for (var l = 1; l < QualityLevel.Max; l++)
            Assert.True(QualityLevel.For(l + 1).ResolutionScale >= QualityLevel.For(l).ResolutionScale);
        Assert.Equal(1, QualityLevel.For(-3).Level);
        Assert.Equal(QualityLevel.Max, QualityLevel.For(99).Level);
    }

    [Fact]
    public void Every_map_rule_encodes_every_level()
    {
        foreach (var id in new[] { "pcsx2", "dolphin", "rpcs3", "ppsspp", "lime3ds", "azahar", "xenia-canary", "ryubing" })
        {
            var map = EmuTuneMaps.For(id)!;
            foreach (var r in map.Rules)
            {
                Assert.True(map.Files.ContainsKey(r.File));
                for (var l = 1; l <= QualityLevel.Max; l++) Assert.NotEmpty(r.Encode(QualityLevel.For(l)));
            }
        }
        Assert.Equal("3", EmuTuneMaps.For("dolphin")!.Rules.First(r => r.Kind == TuneKind.Anisotropy).Encode(QualityLevel.For(4))); // 8x = 3
    }

    [Fact]
    public void Heavier_consoles_and_weaker_gpus_start_lower()
    {
        Assert.True(EmuTuner.Ceiling(GpuTier.High, EmuPlatform.PSP) > EmuTuner.Ceiling(GpuTier.High, EmuPlatform.PlayStation3));
        Assert.True(EmuTuner.Ceiling(GpuTier.High, EmuPlatform.PlayStation2) > EmuTuner.Ceiling(GpuTier.Integrated, EmuPlatform.PlayStation2));
        Assert.InRange(EmuTuner.StartLevel(GpuTier.Integrated, EmuPlatform.Switch), 1, 2);
    }

    [Fact]
    public void Holding_the_target_with_headroom_raises_quality_up_to_the_ceiling()
    {
        var st = new EmuTuningState { Level = 3 };
        var r = EmuTuner.Adjust(st, ceiling: 5, avgFps: 60, onePercentLow: 55, gpuLoad: 70, target: 60, Long);
        Assert.Equal(4, r.Level);
        Assert.True(r.Changed);
        EmuTuner.Adjust(st, 5, 60, 55, 70, 60, Long);
        var top = EmuTuner.Adjust(st, 5, 60, 55, 70, 60, Long);
        Assert.Equal(5, top.Level);
        Assert.False(top.Changed);
    }

    [Fact]
    public void Missing_the_target_lowers_quality_and_remembers_the_failed_level()
    {
        var st = new EmuTuningState { Level = 5 };
        var r = EmuTuner.Adjust(st, 7, avgFps: 41, onePercentLow: 30, gpuLoad: 99, target: 60, Long);
        Assert.Equal(3, r.Level); // average under 75% of target: two steps
        Assert.Equal(5, st.FailedLevel);
        // Doing well at 3 does not climb back into 5 straight away.
        var up = EmuTuner.Adjust(st, 7, 60, 55, 60, 60, Long);
        Assert.Equal(4, up.Level);
        var stay = EmuTuner.Adjust(st, 7, 60, 55, 60, 60, Long);
        Assert.Equal(4, stay.Level);
        Assert.False(stay.Changed);
    }

    [Fact]
    public void Gpu_without_headroom_does_not_climb_and_short_sessions_change_nothing()
    {
        var st = new EmuTuningState { Level = 3 };
        Assert.Equal(3, EmuTuner.Adjust(st, 7, 60, 55, gpuLoad: 97, 60, Long).Level);
        var short1 = EmuTuner.Adjust(st, 7, 10, 5, 99, 60, TimeSpan.FromMinutes(1));
        Assert.False(short1.Changed);
        Assert.Equal(3, short1.Level);
    }

    [Fact]
    public void At_the_lowest_level_it_says_so_instead_of_going_lower()
    {
        var st = new EmuTuningState { Level = 1 };
        var r = EmuTuner.Adjust(st, 5, 20, 15, 99, 60, Long);
        Assert.Equal(1, r.Level);
        Assert.Contains("another emulator", r.Note);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080", true, GpuTier.High)]
    [InlineData("NVIDIA GeForce RTX 3060", true, GpuTier.Mid)]
    [InlineData("AMD Radeon RX 7900 XTX", true, GpuTier.High)]
    [InlineData("AMD Radeon 8060S Graphics", false, GpuTier.Mid)]
    [InlineData("AMD Radeon(TM) Graphics", false, GpuTier.Integrated)]
    [InlineData("Intel(R) UHD Graphics", false, GpuTier.Integrated)]
    [InlineData(null, false, GpuTier.Integrated)]
    public void Gpu_names_map_to_tiers(string? name, bool discrete, GpuTier expected) => Assert.Equal(expected, GpuTierGuess.Classify(name, discrete));

    [Fact]
    public void Config_file_is_found_beside_the_program_first_then_in_user_folders()
    {
        var spec = EmuTuneMaps.For("pcsx2")!.Files["ini"];
        var expected = Path.Combine(@"C:\E", "inis", "PCSX2.ini");
        Assert.Equal(expected, EmuTuneMaps.Locate(spec, @"C:\E", @"C:\A", @"C:\D", p => p == expected));
        Assert.Null(EmuTuneMaps.Locate(spec, @"C:\E", @"C:\A", @"C:\D", _ => false));
    }
}

public class EmulatorAdvisorTests
{
    private static EmuPerfRecord Rec(double avg, double low, double seconds = 1800, int sessions = 3, double atTarget = 0.8) =>
        new() { AvgFps = avg, OnePercentLow = low, Seconds = seconds, Sessions = sessions, TimeAtTarget = atTarget };

    private static readonly IReadOnlyList<EmulatorSource> Sources = EmulatorSources.Defaults;

    [Fact]
    public void Recording_weights_by_time_and_ignores_short_or_fps_less_sessions()
    {
        var perf = new Dictionary<string, EmuPerfRecord>();
        Assert.False(EmulatorAdvisor.Record(perf, "pcsx2", 60, true, 60, 55, 1, 3, DateTimeOffset.Now));
        Assert.False(EmulatorAdvisor.Record(perf, "pcsx2", 900, false, 60, 55, 1, 3, DateTimeOffset.Now));
        Assert.True(EmulatorAdvisor.Record(perf, "pcsx2", 600, true, 60, 50, 1, 3, DateTimeOffset.Now));
        Assert.True(EmulatorAdvisor.Record(perf, "pcsx2", 1800, true, 30, 20, 0, 3, DateTimeOffset.Now));
        Assert.Equal(2, perf["pcsx2"].Sessions);
        Assert.Equal(37.5, perf["pcsx2"].AvgFps, 1);
    }

    [Fact]
    public void Clearly_better_measured_emulator_is_suggested()
    {
        var perf = new Dictionary<string, EmuPerfRecord> { ["lime3ds"] = Rec(40, 28), ["azahar"] = Rec(58, 50) };
        var s = EmulatorAdvisor.Suggest(EmuPlatform.Nintendo3DS, "lime3ds", 60, perf, Sources, new HashSet<string> { "lime3ds", "azahar" }, 3);
        Assert.Equal(SuggestionKind.Measured, s!.Kind);
        Assert.Equal("azahar", s.ToId);
        Assert.Contains("58", s.Message);
    }

    [Fact]
    public void Small_differences_or_thin_data_suggest_nothing()
    {
        Assert.Null(EmulatorAdvisor.Suggest(EmuPlatform.Nintendo3DS, "azahar", 60, new Dictionary<string, EmuPerfRecord> { ["azahar"] = Rec(55, 48), ["lime3ds"] = Rec(57, 49) }, Sources, new HashSet<string> { "azahar", "lime3ds" }, 3));
        Assert.Null(EmulatorAdvisor.Suggest(EmuPlatform.Nintendo3DS, "azahar", 60, new Dictionary<string, EmuPerfRecord> { ["azahar"] = Rec(30, 20), ["lime3ds"] = Rec(59, 55, sessions: 1, seconds: 200) }, Sources, new HashSet<string>(), 3));
    }

    [Fact]
    public void Discontinued_emulator_points_to_its_installed_successor()
    {
        var s = EmulatorAdvisor.Suggest(EmuPlatform.Nintendo3DS, "lime3ds", 60, new Dictionary<string, EmuPerfRecord>(), Sources, new HashSet<string> { "lime3ds", "azahar" }, 3);
        Assert.Equal(SuggestionKind.Successor, s!.Kind);
        Assert.Equal("azahar", s.ToId);
        Assert.Null(EmulatorAdvisor.Suggest(EmuPlatform.Nintendo3DS, "lime3ds", 60, new Dictionary<string, EmuPerfRecord>(), Sources, new HashSet<string> { "lime3ds" }, 3)); // successor not installed
    }

    [Fact]
    public void Struggling_at_the_floor_offers_an_untried_emulator_as_a_trial()
    {
        var perf = new Dictionary<string, EmuPerfRecord> { ["ryubing"] = Rec(18, 12, atTarget: 0.1) };
        var s = EmulatorAdvisor.Suggest(EmuPlatform.Switch, "ryubing", 30, perf, Sources, new HashSet<string> { "ryubing", "yuzu-fork" }, currentLevel: 1);
        Assert.Equal(SuggestionKind.Trial, s!.Kind);
        Assert.Contains("isn't proven", s.Message);
        Assert.Null(EmulatorAdvisor.Suggest(EmuPlatform.Switch, "ryubing", 30, perf, Sources, new HashSet<string> { "ryubing", "yuzu-fork" }, currentLevel: 4)); // not at the floor yet
    }
}

public class EmulatorDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fitz-emu-" + Guid.NewGuid().ToString("N"));
    public EmulatorDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Touch(string rel) { var p = Path.Combine(_root, rel.Replace('\\', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); return p; }

    [Fact]
    public void Finds_emulators_and_prefers_the_shallowest_copy()
    {
        var shallow = Touch(@"Emu\pcsx2\pcsx2-qt.exe");
        Touch(@"Emu\old\backup\deep\pcsx2-qt.exe");
        Touch(@"Emu\Dolphin-x64\Dolphin.exe");
        Touch(@"Emu\random\notanemulator.exe");
        var found = EmulatorDiscovery.Scan([_root], EmulatorSources.Defaults);
        Assert.Equal(shallow, found.First(f => f.Id == "pcsx2").Path);
        Assert.Contains(found, f => f.Id == "dolphin");
        Assert.DoesNotContain(found, f => f.Id == "rpcs3");
    }

    [Fact]
    public void Finds_game_files_by_platform_and_cleans_their_names()
    {
        Touch(@"PS2\Okami (USA) [!].iso");
        Touch(@"PS2\sub\Gran_Turismo_4.chd");
        Touch(@"PS2\readme.txt");
        Touch(@"PS3\Demon\PS3_GAME\USRDIR\EBOOT.BIN");
        var ps2 = EmulatorDiscovery.ScanRoms(Path.Combine(_root, "PS2"), EmuPlatform.PlayStation2);
        Assert.Equal(["Gran Turismo 4", "Okami"], ps2.Select(r => r.Title));
        var ps3 = EmulatorDiscovery.ScanRoms(Path.Combine(_root, "PS3"), EmuPlatform.PlayStation3);
        Assert.Single(ps3);
        Assert.True(ps3[0].IsFolder);
    }

    [Fact]
    public void Library_entry_launches_the_emulator_with_the_game_and_can_switch_emulator()
    {
        var pcsx2 = EmulatorSources.Defaults.First(s => s.Id == "pcsx2");
        var g = EmulationLibrary.Create(new FoundRom(@"D:\Roms\Okami.iso", "Okami", false), EmuPlatform.PlayStation2, pcsx2, @"C:\Emu\pcsx2-qt.exe");
        Assert.Equal(@"C:\Emu\pcsx2-qt.exe", g.ExePath);
        Assert.Contains("\"D:\\Roms\\Okami.iso\"", g.Arguments);
        Assert.Equal(60, g.FpsTarget);
        var rpcs3 = EmulatorSources.Defaults.First(s => s.Id == "rpcs3");
        EmulationLibrary.Point(g, rpcs3, @"C:\R\rpcs3.exe");
        Assert.Equal("rpcs3", g.EmulatorId);
        Assert.StartsWith("--no-gui", g.Arguments);
        Assert.Equal(@"D:\Roms\Okami.iso", g.RomPath);
    }
}
