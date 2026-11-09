using System.Text.Json;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Tests;

public class EmulationModelTests
{
    [Fact]
    public void Every_platform_has_an_emulator_that_exists_and_runs_it()
    {
        var ids = EmulatorSources.Defaults.ToDictionary(e => e.Id);
        foreach (var p in Platforms.All)
        {
            Assert.NotEmpty(p.EmulatorIds);
            foreach (var id in p.EmulatorIds) Assert.Contains(p.Platform, ids[id].Platforms);
        }
        Assert.Equal(Enum.GetValues<EmuPlatform>().Length, Platforms.All.Count);
    }

    [Fact]
    public void Extensions_find_every_platform_that_uses_them()
    {
        Assert.Equal([EmuPlatform.Nintendo3DS], Platforms.ByExtension("3ds").Select(p => p.Platform));
        var iso = Platforms.ByExtension(".ISO").Select(p => p.Platform).ToList();
        Assert.Contains(EmuPlatform.PlayStation2, iso);
        Assert.Contains(EmuPlatform.GameCube, iso);
        Assert.Contains(EmuPlatform.Xbox360, iso);
        Assert.Empty(Platforms.ByExtension(".exe"));
    }

    [Fact]
    public void Default_sources_are_consistent_and_unverified_until_checked()
    {
        Assert.Equal(EmulatorSources.Defaults.Count, EmulatorSources.Defaults.Select(e => e.Id).Distinct().Count());
        foreach (var e in EmulatorSources.Defaults)
        {
            Assert.NotEmpty(e.ExeNames);
            Assert.False(e.Verified);
            if (e.Type != EmulatorSourceType.GitHub) continue;
            Assert.Matches(@"^[\w.-]+/[\w.-]+$", e.Repo!);
            _ = new Regex(e.AssetPattern!); // must compile
        }
        Assert.All(EmulatorSources.Defaults.Where(e => e.Platforms.Contains(EmuPlatform.Switch)), e => Assert.Equal(EmulatorSourceType.Manual, e.Type));
    }

    [Fact]
    public void Edited_sources_replace_or_add_and_bad_json_keeps_defaults()
    {
        var (merged, err) = EmulatorSources.Merge("""{"emulators":[{"id":"pcsx2","name":"PCSX2","exeNames":["x.exe"]},{"id":"mine","name":"Mine"}]}""");
        Assert.Null(err);
        Assert.Equal(EmulatorSources.Defaults.Count + 1, merged.Count);
        Assert.Equal(["x.exe"], merged.First(e => e.Id == "pcsx2").ExeNames);

        var (kept, bad) = EmulatorSources.Merge("{ not json");
        Assert.NotNull(bad);
        Assert.Equal(EmulatorSources.Defaults.Count, kept.Count);
        Assert.Null(EmulatorSources.Merge(null).Error);
        Assert.NotNull(EmulatorSources.Merge("""{"emulators":[{"name":"no id"}]}""").Error);
    }

    [Fact]
    public void Sources_survive_a_round_trip()
    {
        var (back, err) = EmulatorSources.Merge(EmulatorSources.ToJson(EmulatorSources.Defaults));
        Assert.Null(err);
        Assert.Equal(EmulatorSources.Defaults.Count, back.Count);
        Assert.Equal(EmulatorSourceType.Website, back.First(e => e.Id == "dolphin").Type);
    }

    [Fact]
    public void Old_library_and_settings_files_still_load_with_empty_emulation_fields()
    {
        var game = JsonSerializer.Deserialize<Game>("""{"Name":"Old game","ExePath":"C:\\g.exe"}""")!;
        Assert.Null(game.Platform);
        Assert.Null(game.RomPath);
        Assert.Empty(game.Collections);

        var s = JsonSerializer.Deserialize<LauncherSettings>("{}")!;
        Assert.Equal(6, s.Emulation.CheckIntervalHours);
        Assert.Empty(s.Emulation.ScanFolders);
    }

    [Fact]
    public void Emulation_fields_round_trip()
    {
        var g = new Game { Name = "x", Platform = EmuPlatform.PlayStation2, EmulatorId = "pcsx2", RomPath = "D:\\a.iso", TitleId = "SLUS-20001", Collections = ["Favourites"], TuningPreset = "balanced" };
        var back = JsonSerializer.Deserialize<Game>(JsonSerializer.Serialize(g))!;
        Assert.Equal(EmuPlatform.PlayStation2, back.Platform);
        Assert.Equal("SLUS-20001", back.TitleId);
        Assert.Equal(["Favourites"], back.Collections);
    }
}
