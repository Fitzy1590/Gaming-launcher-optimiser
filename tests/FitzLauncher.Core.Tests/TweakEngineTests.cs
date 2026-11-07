using FitzLauncher.Core.Artwork;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Core.Tests;

internal sealed class FakeRegistry : IRegistryAccess
{
    public readonly Dictionary<string, string> Values = [];
    private static string K(RegistryHive h, string key, string name) => $"{h}|{key}|{name}";
    public string? GetString(RegistryHive hive, string key, string name) => Values.GetValueOrDefault(K(hive, key, name));
    public void Set(RegistryHive hive, string key, string name, string value, RegistryValueType type) => Values[K(hive, key, name)] = value;
    public void Delete(RegistryHive hive, string key, string name) => Values.Remove(K(hive, key, name));
}

public class TweakEngineTests
{
    private const string Original = "[SystemSettings]\r\nr.Foo=1\r\n";

    private static Tweak IniTweak(string file, string value) => new()
    {
        Id = "cap", Title = "cap", Description = "",
        Actions = [new IniSetAction { FilePath = file, Section = "SystemSettings", Key = "t.MaxFPS", Value = value }],
    };

    [Fact]
    public void Apply_then_revert_restores_file_byte_for_byte_and_backs_up()
    {
        using var d = new TestDir();
        var file = d.File("cfg/Engine.ini", Original);
        var engine = new TweakEngine(new FakeRegistry(), d.Path_("backups"));
        var ledger = new List<AppliedChange>();

        Assert.True(engine.Apply(ledger, IniTweak(file, "117")).Success);
        Assert.Contains("t.MaxFPS=117", File.ReadAllText(file));
        Assert.True(File.Exists(Assert.Single(ledger).BackupPath));
        Assert.True(TweakEngine.IsApplied(ledger, IniTweak(file, "117")));

        Assert.Empty(engine.RevertAll(ledger));
        Assert.Equal(Original, File.ReadAllText(file));
        Assert.Empty(ledger);
    }

    [Fact]
    public void Reoptimising_at_new_target_keeps_the_true_original_for_revert()
    {
        using var d = new TestDir();
        var file = d.File("cfg/Engine.ini", "[SystemSettings]\nt.MaxFPS=60\n");
        var engine = new TweakEngine(new FakeRegistry(), d.Path_("backups"));
        var ledger = new List<AppliedChange>();

        engine.Apply(ledger, IniTweak(file, "117"));
        engine.Apply(ledger, IniTweak(file, "100"));
        Assert.Single(ledger);
        Assert.Contains("t.MaxFPS=100", File.ReadAllText(file));
        Assert.False(TweakEngine.IsApplied(ledger, IniTweak(file, "117")));

        engine.RevertAll(ledger);
        Assert.Equal("[SystemSettings]\nt.MaxFPS=60\n", File.ReadAllText(file));
    }

    [Fact]
    public void Missing_config_folder_fails_cleanly()
    {
        using var d = new TestDir();
        var engine = new TweakEngine(new FakeRegistry(), d.Path_("backups"));
        var result = engine.Apply([], IniTweak(d.Path_("nope", "Engine.ini"), "117"));
        Assert.False(result.Success);
    }

    [Fact]
    public void DirectX_gpu_preferences_merge_and_unmerge_pairs()
    {
        var reg = new FakeRegistry();
        const string key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        reg.Set(RegistryHive.CurrentUser, key, @"C:\g.exe", "AutoHDREnable=1;", RegistryValueType.String);
        var engine = new TweakEngine(reg, Path.GetTempPath());
        var ledger = new List<AppliedChange>();
        RegistrySetAction Pref(string v) => new() { Hive = RegistryHive.CurrentUser, KeyPath = key, ValueName = @"C:\g.exe", Value = v, MergeSemicolonPairs = true };

        engine.Apply(ledger, new Tweak { Id = "gpu", Title = "", Description = "", Actions = [Pref("GpuPreference=2;")] });
        engine.Apply(ledger, new Tweak { Id = "swap", Title = "", Description = "", Actions = [Pref("SwapEffectUpgradeEnable=1;")] });
        Assert.Equal("AutoHDREnable=1;GpuPreference=2;SwapEffectUpgradeEnable=1;", reg.GetString(RegistryHive.CurrentUser, key, @"C:\g.exe"));

        engine.RevertAll(ledger, tweakId: "gpu");
        Assert.Equal("AutoHDREnable=1;SwapEffectUpgradeEnable=1;", reg.GetString(RegistryHive.CurrentUser, key, @"C:\g.exe"));
        engine.RevertAll(ledger);
        Assert.Equal("AutoHDREnable=1;", reg.GetString(RegistryHive.CurrentUser, key, @"C:\g.exe"));
    }

    [Fact]
    public void Launch_arguments_replace_previous_value_and_revert()
    {
        var game = new Game { OptimizerArguments = "-novid" };
        var engine = new TweakEngine(new FakeRegistry(), Path.GetTempPath());
        var ledger = new List<AppliedChange>();
        Tweak Cap(int n) => new() { Id = "cap", Title = "", Description = "", Actions = [new LaunchArgumentAction { Arguments = $"+fps_max {n}" }] };

        engine.Apply(ledger, Cap(117), game);
        engine.Apply(ledger, Cap(90), game);
        Assert.Equal("-novid +fps_max 90", game.OptimizerArguments);
        engine.RevertAll(ledger, game);
        Assert.Equal("-novid", game.OptimizerArguments);
    }

    [Fact]
    public void Rtss_profile_is_created_and_removed()
    {
        using var d = new TestDir();
        d.Dir("RTSS/Profiles");
        var engine = new TweakEngine(new FakeRegistry(), d.Path_("b"));
        var ledger = new List<AppliedChange>();
        var tweak = new Tweak { Id = "cap", Title = "", Description = "", Actions = [new RtssFrameLimitAction { RtssDirectory = d.Path_("RTSS"), ExeName = "game.exe", Limit = 117 }] };

        engine.Apply(ledger, tweak);
        var profile = d.Path_("RTSS", "Profiles", "game.exe.cfg");
        Assert.Contains("[Framerate]\nLimit=117", File.ReadAllText(profile));
        engine.RevertAll(ledger);
        Assert.False(File.Exists(profile));
    }

    [Fact]
    public void Manual_tweaks_are_not_applied()
    {
        var engine = new TweakEngine(new FakeRegistry(), Path.GetTempPath());
        var r = engine.Apply([], new Tweak { Id = "m", Title = "", Description = "", Actions = [new ManualStepAction { Instructions = "x" }] });
        Assert.False(r.Success);
    }
}

public class StorageTests
{
    [Fact]
    public void Library_round_trips_polymorphic_changes()
    {
        using var d = new TestDir();
        var store = new LibraryStore(new AppPaths(d.Root));
        var data = new LibraryData();
        var game = new Game { Name = "G", FpsTarget = 117, TotalPlayTime = TimeSpan.FromMinutes(90) };
        game.AppliedChanges.Add(new AppliedChange { TweakId = "cap", Action = new RtssFrameLimitAction { ExeName = "g.exe", Limit = 117 } });
        game.AppliedChanges.Add(new AppliedChange { TweakId = "ini", Action = new IniSetAction { FilePath = "a", Key = "k", Value = "v" } });
        data.Games.Add(game);
        store.SaveLibrary(data);

        var loaded = store.LoadLibrary().Games.Single();
        Assert.Equal(TimeSpan.FromMinutes(90), loaded.TotalPlayTime);
        Assert.IsType<RtssFrameLimitAction>(loaded.AppliedChanges[0].Action);
        Assert.Equal("v", Assert.IsType<IniSetAction>(loaded.AppliedChanges[1].Action).Value);
    }

    [Fact]
    public void Sessions_append_and_filter_by_game()
    {
        using var d = new TestDir();
        var store = new LibraryStore(new AppPaths(d.Root));
        var a = Guid.NewGuid();
        store.AppendSession(new SessionSummary { GameId = a, AverageFps = 110, FpsTimeline = [100, 110] });
        store.AppendSession(new SessionSummary { GameId = Guid.NewGuid(), AverageFps = 60 });
        var mine = store.LoadSessions(a);
        Assert.Equal(110, Assert.Single(mine).AverageFps);
        Assert.Equal(2, store.LoadSessions().Count);
    }
}

public class ArtworkTests
{
    [Fact]
    public void Picks_exact_steam_match_over_first_result()
    {
        const string json = """{"total":2,"items":[{"type":"app","name":"ELDEN RING NIGHTREIGN","id":2622380},{"type":"app","name":"ELDEN RING","id":1245620}]}""";
        Assert.Equal(1245620, ArtworkService.PickSteamMatch(json, "Elden Ring")!.Value.id);
    }

    [Fact]
    public void No_apps_means_no_match() =>
        Assert.Null(ArtworkService.PickSteamMatch("""{"total":0,"items":[]}""", "x"));
}
