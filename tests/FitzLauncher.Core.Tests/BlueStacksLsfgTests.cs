using FitzLauncher.Core.Emulators;

namespace FitzLauncher.Core.Tests;

public class BlueStacksLsfgTests
{
    [Fact]
    public void Instances_are_read_from_the_config()
    {
        var conf = "bst.instance.Pie64.display_name=\"Pie64\"\nbst.instance.Pie64.adb_port=\"5555\"\nbst.instance.Pie64_1.display_name=\"Second\"\nbst.other=1\n";
        var list = BlueStacks.ParseInstances(conf);
        Assert.Equal(2, list.Count);
        Assert.Equal(5555, list[0].AdbPort);
        Assert.Equal("Second", list[1].Name);
        Assert.Null(list[1].AdbPort);
    }

    [Fact]
    public void Launch_arguments_open_the_app_directly()
        => Assert.Equal("--instance Pie64 --cmd launchApp --package jp.pokemon.pokemontcgp", BlueStacks.LaunchArguments("Pie64", "jp.pokemon.pokemontcgp"));

    [Theory]
    [InlineData("a b")]
    [InlineData("com.x; calc")]
    [InlineData("nodots")]
    [InlineData("")]
    public void Odd_package_names_never_reach_a_command_line(string pkg)
        => Assert.Throws<ArgumentException>(() => BlueStacks.LaunchArguments("Pie64", pkg));

    [Fact]
    public void Instance_name_is_checked_too()
        => Assert.Throws<ArgumentException>(() => BlueStacks.LaunchArguments("Pie64 --evil", "com.a.b"));

    [Fact]
    public void Package_list_skips_system_and_bluestacks_apps_and_names_games()
    {
        var apps = BlueStacks.ParsePackages("package:jp.pokemon.pokemontcgp\r\npackage:com.bluestacks.settings\npackage:com.google.android.gms\npackage:com.cool.my_game\npackage:jp.pokemon.pokemontcgp\ngarbage\n");
        Assert.Equal(["My game", "Pokémon TCG Pocket"], apps.Select(a => a.Name));
    }

    [Fact]
    public void Plan_doubles_the_base_rate_and_clamps_inputs()
    {
        Assert.Equal(120, Lsfg.Plan().OutputFps);
        Assert.Equal(2, Lsfg.Plan(multiplier: 4).Multiplier);
        Assert.Equal(99, Lsfg.Plan(gpuCeiling: 120).GpuCeilingPercent);
        Assert.Equal(30, Lsfg.Plan(10).BaseFps);
    }

    [Fact]
    public void Gpu_guard_steps_down_when_maxed_and_back_up_with_room()
    {
        var g = new Lsfg.GpuGuard(Lsfg.Plan());
        Assert.Null(g.Update(99)); Assert.Null(g.Update(99));
        Assert.Equal(55, g.Update(99));
        for (var i = 0; i < 9; i++) Assert.Null(g.Update(60));
        Assert.Equal(60, g.Update(60));
        Assert.Null(g.Update(null));
    }

    [Fact]
    public void Gpu_guard_never_goes_below_its_floor()
    {
        var g = new Lsfg.GpuGuard(Lsfg.Plan(), floorFps: 50);
        for (var i = 0; i < 60; i++) g.Update(100);
        Assert.Equal(50, g.CurrentCap);
    }

    [Fact]
    public void Settings_patch_changes_only_existing_keys_and_reverts()
    {
        var xml = "<Settings><LSFG3Multiplier>3</LSFG3Multiplier><Other>x</Other></Settings>";
        var (patched, changes) = Lsfg.Patch(xml, Lsfg.Plan());
        Assert.Single(changes);
        Assert.Contains("<LSFG3Multiplier>2</LSFG3Multiplier>", patched);
        Assert.Contains("<Other>x</Other>", patched);
        Assert.Equal(xml, Lsfg.Revert(patched, changes));
    }

    [Fact]
    public void Unknown_settings_file_is_left_alone()
    {
        var (same, changes) = Lsfg.Patch("<Settings><Foo>1</Foo></Settings>", Lsfg.Plan());
        Assert.Empty(changes);
        Assert.Equal("<Settings><Foo>1</Foo></Settings>", same);
        Assert.Equal("not xml", Lsfg.Patch("not xml", Lsfg.Plan()).Xml);
    }

    [Fact]
    public void Revert_skips_values_the_user_changed_since()
    {
        var (patched, changes) = Lsfg.Patch("<S><LSFG3Multiplier>3</LSFG3Multiplier></S>", Lsfg.Plan());
        var edited = patched.Replace(">2<", ">4<");
        Assert.Equal(edited, Lsfg.Revert(edited, changes));
    }
}
