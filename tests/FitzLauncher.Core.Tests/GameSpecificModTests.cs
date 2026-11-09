using FitzLauncher.Core.Mods;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class GameSpecificModTests
{
    private static readonly GameModLayout W3 = new() { Framework = ModFramework.Witcher3, ExeDir = "bin/x64_dx12" };
    private static bool None(string _) => false;

    [Fact]
    public void Witcher3ModsGoInTheModsFolder()
    {
        var a = ModInstallPlanner.Plan(["modFriendlyHUD/content/scripts/game/hud.ws"], W3, "Friendly HUD", None);
        Assert.Equal("mods/modFriendlyHUD/content/scripts/game/hud.ws", a.Files[0].Dest);

        var b = ModInstallPlanner.Plan(["content/blob0.bundle"], W3, "HD Reworked", None);
        Assert.StartsWith("mods/modHDReworked/content/", b.Files[0].Dest);

        var c = ModInstallPlanner.Plan(["dlcHairstyles/content/x.bundle"], W3, "Hair", None);
        Assert.StartsWith("dlc/dlcHairstyles/", c.Files[0].Dest);
    }

    [Fact]
    public void Witcher3ScriptConflictsNeedScriptMergerUnlessMerged()
    {
        using var t = new TestDir();
        var root = t.Dir("game");
        t.File("game/mods/modA/content/scripts/game/player.ws", "a");
        t.File("game/mods/modB/content/scripts/game/player.ws", "b");
        t.File("game/mods/modC/content/scripts/game/hud.ws", "c");
        t.File("game/mods/modD/content/scripts/game/hud.ws", "d");
        t.File("game/mods/mod0000_MergedFiles/content/scripts/game/hud.ws", "merged");
        t.File("game/mods/Unprefixed/content/x.bundle", "x");

        var issues = ModCompatibility.Witcher3(root).ToList();
        Assert.Contains(issues, i => i.Title.Contains("modA + modB") && i.Fix!.Contains("Script Merger"));
        Assert.DoesNotContain(issues, i => i.Title.Contains("modC")); // already merged
        Assert.Contains(issues, i => i.Title.Contains("Unprefixed"));
    }

    [Fact]
    public void CyberpunkOldPatchFolderAndXlRequirements()
    {
        using var t = new TestDir();
        var root = t.Dir("game");
        t.File("game/archive/pc/patch/old.archive", "x");
        Assert.Single(ModCompatibility.Cyberpunk(root));

        var req = ModInstallPlanner.DetectRequires(["archive/pc/mod/outfit.archive.xl", "r6/tweaks/items.yaml"], new GameModLayout { Framework = ModFramework.Cyberpunk }).ToList();
        Assert.Contains("ArchiveXL", req);
        Assert.Contains("TweakXL", req);
    }

    [Fact]
    public void FlagsModsFromBeforeTheBigUpdates()
    {
        var oldMod = new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var nextGenGame = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var r = ModPreInstallCheck.Assess(W3, null, oldMod, nextGenGame);
        Assert.Equal(CompatVerdict.Problem, r.Verdict);
        Assert.Equal("Not compatible", r.Summary);
        Assert.Contains(r.Reasons, x => x.Contains("next-gen"));

        var cp = ModPreInstallCheck.Assess(new GameModLayout { Framework = ModFramework.Cyberpunk }, null, new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), nextGenGame);
        Assert.Contains(cp.Reasons, x => x.Contains("2.0"));
    }
}

public class Witcher3WrapperTests
{
    [Fact]
    public void AWrapperNamedLikeAModIsStillStripped()
    {
        var layout = new GameModLayout { Framework = ModFramework.Witcher3, ExeDir = "bin/x64_dx12" };
        var p = ModInstallPlanner.Plan(["Modern UI 1.0/modModernUI/content/x.bundle"], layout, "Modern UI", _ => false);
        Assert.Equal("mods/modModernUI/content/x.bundle", p.Files[0].Dest);
    }
}
