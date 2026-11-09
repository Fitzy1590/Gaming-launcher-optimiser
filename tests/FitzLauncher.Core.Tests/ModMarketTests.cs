using FitzLauncher.Core.Mods;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class ModMarketTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RankingsMapToNexusSortsAndTimeWindows()
    {
        var (f, s) = NexusQuery.Build("witcher3", null, ModRanking.PopularThisMonth, Now);
        Assert.Equal("downloads", s[0]!.AsObject().First().Key);
        Assert.Equal(Now.AddDays(-30).ToUnixTimeSeconds().ToString(), f["createdAt"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("GTE", f["createdAt"]![0]!["op"]!.GetValue<string>());

        (f, s) = NexusQuery.Build("witcher3", "  hd reworked ", ModRanking.Relevance, Now);
        Assert.Equal("relevance", s[0]!.AsObject().First().Key);
        Assert.Equal("hd reworked", f["nameStemmed"]![0]!["value"]!.GetValue<string>());

        (_, s) = NexusQuery.Build("witcher3", null, ModRanking.Relevance, Now);
        Assert.Equal("endorsements", s[0]!.AsObject().First().Key); // no query: best-loved first
        (f, s) = NexusQuery.Build("witcher3", null, ModRanking.RecentlyUpdated, Now);
        Assert.NotNull(f["updatedAt"]);
        (_, s) = NexusQuery.Build("witcher3", null, ModRanking.Newest, Now);
        Assert.Equal("createdAt", s[0]!.AsObject().First().Key);
    }

    [Fact]
    public async Task ResolverInstallsRequirementsFirstAndSkipsInstalled()
    {
        // 1 needs 2 and 3; 2 needs 4; 3 is installed; 4 needs an external tool; 1 needs DLC.
        var db = new Dictionary<long, ModRequirementInfo>
        {
            [1] = new(1, "Main", [new(2, "Lib A", null, false, null), new(3, "Lib B", null, false, null)], ["Blood and Wine"]),
            [2] = new(2, "Lib A", [new(4, "Core", null, false, null)], []),
            [4] = new(4, "Core", [new(null, "Script Hook", "https://example.org/sh", true, null)], []),
        };
        var plan = await RequirementResolver.ResolveAsync(1, "Main", id => Task.FromResult(db.GetValueOrDefault(id)), id => id == 3);
        Assert.Equal([4L, 2L, 1L], plan.Install.Select(x => x.ModId));
        Assert.Equal(3, plan.AlreadyInstalled.Single().ModId);
        Assert.Equal("Script Hook", plan.External.Single().Name);
        Assert.Equal("Blood and Wine", plan.Dlc.Single());
    }

    [Fact]
    public async Task ResolverSurvivesCycles()
    {
        var db = new Dictionary<long, ModRequirementInfo>
        {
            [1] = new(1, "A", [new(2, "B", null, false, null)], []),
            [2] = new(2, "B", [new(1, "A", null, false, null)], []),
        };
        var plan = await RequirementResolver.ResolveAsync(1, "A", id => Task.FromResult(db.GetValueOrDefault(id)), _ => false);
        Assert.Equal([2L, 1L], plan.Install.Select(x => x.ModId));
        Assert.Contains(plan.Notes, n => n.Contains("loop"));
    }

    [Fact]
    public void CompatibilityFlagsGameUpdatesExternalsAndAntiCheat()
    {
        var clean = ModPreInstallCheck.Assess(new GameModLayout(), null, Now.AddDays(-5), Now.AddDays(-30));
        Assert.Equal(CompatVerdict.Good, clean.Verdict);

        var plan = new RequirementPlan();
        plan.External.Add(new RequiredMod(null, "SKSE", "https://skse.silverlock.org", true, null));
        var r = ModPreInstallCheck.Assess(new GameModLayout { HasAntiCheat = true }, plan, Now.AddDays(-60), Now.AddDays(-1), deprecated: false);
        Assert.Equal(CompatVerdict.Check, r.Verdict);
        Assert.Contains(r.Reasons, x => x.Contains("SKSE"));
        Assert.Contains(r.Reasons, x => x.Contains("game was updated"));
        Assert.Contains(r.Reasons, x => x.Contains("anti-cheat"));

        Assert.Equal(CompatVerdict.Problem, ModPreInstallCheck.Assess(new GameModLayout(), null, null, null, deprecated: true).Verdict);
    }
}

public class ModAutoFixTests
{
    [Fact]
    public void FlagsRemovedFilesForRedeployAndDisabledRequirementsForEnabling()
    {
        using var t = new TestDir();
        var root = t.Dir("game");
        t.File("stage/main/plugins/Main.dll", "x");
        t.File("stage/lib/plugins/Lib.dll", "x");
        t.File("game/plugins/Lib.dll", "x"); // lib deployed; main's file was removed by a game update
        var lib = new FitzLauncher.Core.Models.InstalledMod { Name = "Lib", Source = FitzLauncher.Core.Models.ModSource.Nexus, SourceId = "20", Files = ["plugins/Lib.dll"], Enabled = false };
        var main = new FitzLauncher.Core.Models.InstalledMod
        {
            Name = "Main", Source = FitzLauncher.Core.Models.ModSource.Nexus, SourceId = "10", Files = ["plugins/Main.dll"],
            NexusRequirements = [new(20, "Lib"), new(30, "Missing Lib")],
        };
        var game = new FitzLauncher.Core.Models.Game { Name = "G", ExePath = Path.Combine(root, "g.exe") };
        game.ModSetup.Mods.AddRange([lib, main]);

        var issues = ModCompatibility.Check(new ModCheckContext
        {
            Game = game, Layout = new GameModLayout(), GameRoot = root,
            StagedPath = (m, rel) => Path.Combine(t.Root, "stage", m.Name.ToLowerInvariant(), rel.Replace('/', Path.DirectorySeparatorChar)),
        });

        Assert.Contains(issues, i => i.AutoFix == AutoFix.Redeploy && i.FixModId == main.Id);
        Assert.Contains(issues, i => i.AutoFix == AutoFix.EnableMod && i.FixModId == lib.Id);
        Assert.Contains(issues, i => i.Title.Contains("Missing Lib") && i.AutoFix == AutoFix.None);
    }
}
