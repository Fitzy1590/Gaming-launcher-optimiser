using FitzLauncher.Core.Input;

namespace FitzLauncher.Core.Tests;

public class DoubleInputCheckTests
{
    private static InputCheckFacts Facts(bool menu = true, bool psConsole = false, bool mirror = true, bool settingsOn = false,
        bool vigem = true, bool hid = true, int xinput = 1, int sony = 0, params string[] tools) =>
        new(menu, psConsole, mirror, settingsOn, vigem, hid, xinput, sony, tools);

    [Fact]
    public void Clean_set_up_reports_no_problem()
    {
        var r = DoubleInputCheck.Evaluate(Facts());
        Assert.Equal(InputSeverity.Ok, r[0].Severity);
        Assert.DoesNotContain(r, x => x.Severity is InputSeverity.Problem or InputSeverity.Warning);
    }

    [Fact]
    public void Keyboard_mirror_without_vigem_is_a_problem_with_a_fix()
    {
        var r = DoubleInputCheck.Evaluate(Facts(psConsole: true, mirror: true, vigem: false, sony: 1, xinput: 0));
        var p = Assert.Single(r, x => x.Severity == InputSeverity.Problem);
        Assert.Equal(InputFix.TurnOffConsoleMirror, p.Fix);
    }

    [Fact]
    public void Mirror_with_vigem_installed_is_fine()
    {
        var r = DoubleInputCheck.Evaluate(Facts(psConsole: true, mirror: true, vigem: true, sony: 1, xinput: 0));
        Assert.DoesNotContain(r, x => x.Severity == InputSeverity.Problem);
    }

    [Fact]
    public void Same_pad_as_xbox_and_playstation_warns_and_offers_controller_settings_when_drivers_exist()
    {
        var r = DoubleInputCheck.Evaluate(Facts(xinput: 1, sony: 1, tools: "DS4Windows"));
        var w = Assert.Single(r, x => x.Severity == InputSeverity.Warning);
        Assert.Equal(InputFix.TurnOnControllerSettings, w.Fix);
        Assert.Contains("DS4Windows", w.Detail);
    }

    [Fact]
    public void Same_pad_two_ways_without_drivers_has_advice_but_no_automatic_fix()
    {
        var r = DoubleInputCheck.Evaluate(Facts(xinput: 1, sony: 1, hid: false));
        var w = Assert.Single(r, x => x.Severity == InputSeverity.Warning);
        Assert.Equal(InputFix.None, w.Fix);
    }

    [Fact]
    public void Controller_settings_already_on_do_not_warn_about_two_routes()
    {
        var r = DoubleInputCheck.Evaluate(Facts(xinput: 1, sony: 1, settingsOn: true));
        Assert.DoesNotContain(r, x => x.Severity == InputSeverity.Warning);
    }

    [Fact]
    public void Several_xbox_pads_and_no_pad_are_notes_only()
    {
        Assert.Contains(DoubleInputCheck.Evaluate(Facts(xinput: 2)), x => x.Title.StartsWith("2 Xbox"));
        Assert.Contains(DoubleInputCheck.Evaluate(Facts(xinput: 0)), x => x.Title == "No controller found");
    }

    [Fact]
    public void Pre_launch_runs_only_when_a_controller_is_connected()
    {
        Assert.False(DoubleInputCheck.HasController(Facts(xinput: 0, sony: 0)));
        Assert.True(DoubleInputCheck.HasController(Facts(xinput: 0, sony: 1)));
        Assert.True(DoubleInputCheck.HasController(Facts(xinput: 1, sony: 0)));
    }

    [Fact]
    public void Pre_launch_applies_each_fix_once_and_skips_notes_and_unfixable_warnings()
    {
        var both = DoubleInputCheck.Evaluate(Facts(psConsole: true, mirror: true, vigem: false, xinput: 1, sony: 1, hid: false));
        Assert.Equal([InputFix.TurnOffConsoleMirror], DoubleInputCheck.PreLaunchFixes(both));

        var combine = DoubleInputCheck.PreLaunchFixes(DoubleInputCheck.Evaluate(Facts(xinput: 1, sony: 1)));
        Assert.Equal([InputFix.TurnOnControllerSettings], combine);

        Assert.Empty(DoubleInputCheck.PreLaunchFixes(DoubleInputCheck.Evaluate(Facts())));
    }

    [Fact]
    public void Tools_are_matched_by_process_name_once_each()
    {
        var m = DoubleInputCheck.MatchTools(["DS4Windows", "ds4windows", "explorer", "DSX", "dualsensex"]);
        Assert.Equal(["DS4Windows", "DualSense X"], m);
    }
}
