namespace FitzLauncher.Core.Input;

public enum InputSeverity { Ok, Info, Warning, Problem }

/// <summary>A change the check can make to the launcher's own settings (never to another app).</summary>
public enum InputFix { None, TurnOffConsoleMirror, TurnOnControllerSettings }

public sealed record InputFinding(InputSeverity Severity, string Title, string Detail, InputFix Fix = InputFix.None, string FixLabel = "");

/// <summary>What the check can see: launcher settings, installed drivers, connected pads and running input tools.</summary>
public sealed record InputCheckFacts(
    bool MenuNavigation,
    bool PsButtonOpensConsole,
    bool ConsolePadMirror,
    bool ControllerSettingsOn,
    bool ViGEmInstalled,
    bool HidHideInstalled,
    int XInputPads,
    int PlayStationPads,
    IReadOnlyList<string> RunningInputTools);

/// <summary>
/// Looks for the settings and set-ups that make one button press arrive twice, and says what to do about each.
/// Pure: the caller gathers the facts and applies any fix.
/// </summary>
public static class DoubleInputCheck
{
    /// <summary>Process name (lower case, no .exe) → what to call it. These re-present a pad to Windows as a second controller.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownTools = new Dictionary<string, string>
    {
        ["ds4windows"] = "DS4Windows",
        ["dualsensex"] = "DualSense X",
        ["dsx"] = "DualSense X",
        ["inputmapper"] = "InputMapper",
        ["rewasd"] = "reWASD",
        ["x360ce"] = "x360ce",
        ["joytokey"] = "JoyToKey",
        ["antimicrox"] = "antimicrox",
        ["steam"] = "Steam (Steam Input can add its own virtual controller)",
        ["handheldcompanion"] = "Handheld Companion",
    };

    /// <summary>The friendly names of the known tools among the running process names (each once, in a stable order).</summary>
    public static List<string> MatchTools(IEnumerable<string> processNames) =>
        processNames.Select(n => KnownTools.TryGetValue(n.ToLowerInvariant(), out var f) ? f : null)
            .OfType<string>().Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when at least one controller (Xbox-style or PlayStation) is connected, so a pre-launch check is worth running.</summary>
    public static bool HasController(InputCheckFacts f) => f.XInputPads + f.PlayStationPads > 0;

    /// <summary>The launcher-settings fixes to apply automatically before a game starts (each once). Warnings and problems only.</summary>
    public static List<InputFix> PreLaunchFixes(IEnumerable<InputFinding> findings) =>
        findings.Where(x => x.Fix != InputFix.None && x.Severity is InputSeverity.Problem or InputSeverity.Warning)
            .Select(x => x.Fix).Distinct().ToList();

    public static List<InputFinding> Evaluate(InputCheckFacts f)
    {
        var list = new List<InputFinding>();
        var haveDrivers = f.ViGEmInstalled && f.HidHideInstalled;
        var pads = f.XInputPads + f.PlayStationPads;

        if (pads == 0)
            list.Add(new(InputSeverity.Info, "No controller found", "Plug in or switch on your controller, then run the check again so it can look at what Windows sees."));

        // OneXConsole reads the real pad itself, so injected arrow keys / Enter / Esc arrive on top of it.
        if (f.PsButtonOpensConsole && f.ConsolePadMirror && !f.ViGEmInstalled)
            list.Add(new(InputSeverity.Problem, "OneXConsole can get each press twice",
                "While OneXConsole is open, the launcher sends the DualShock's D-pad, Cross and Circle as keyboard keys because ViGEmBus isn't installed. OneXConsole also reads the pad directly, so each press counts twice. " +
                "Turning this off leaves OneXConsole reading the pad on its own; installing ViGEmBus is the other fix.",
                InputFix.TurnOffConsoleMirror, "Turn off keyboard mirroring"));

        // The same DualShock/DualSense visible as a PlayStation pad and as an Xbox pad: something is emulating the second one.
        var tools = f.RunningInputTools;
        if (f.PlayStationPads > 0 && f.XInputPads > 0 && !f.ControllerSettingsOn)
        {
            var who = tools.Count > 0 ? string.Join(", ", tools) + " is running, which may be creating the extra Xbox controller. " : "";
            list.Add(new(InputSeverity.Warning, "One controller may look like two to games",
                "Windows shows an Xbox-style controller and a PlayStation controller at the same time. " + who +
                "A game that listens to every controller then sees each press twice. The launcher's own menus merge the two, so this shows up in games. " +
                (haveDrivers
                    ? "Controller settings combine them into one virtual controller and hide the real ones while a game runs."
                    : "Fix: close the tool that emulates the extra controller, or install ViGEmBus and HidHide so the launcher can combine them."),
                haveDrivers ? InputFix.TurnOnControllerSettings : InputFix.None,
                haveDrivers ? "Turn on controller settings" : ""));
        }
        else if (tools.Count > 0)
            list.Add(new(InputSeverity.Info, "Input tools running: " + string.Join(", ", tools),
                "These can add a second virtual controller. If a game still doubles presses, close them (or turn off their controller emulation) and try again. The launcher won't close other apps for you."));

        if (f.XInputPads > 1)
            list.Add(new(InputSeverity.Info, $"{f.XInputPads} Xbox-style controllers connected",
                "A handheld's built-in controls plus a paired pad (or a virtual one from another tool) both send presses. The launcher's menus use the first one only; a game may listen to all. Disconnect the one you aren't using."));

        if (!f.MenuNavigation)
            list.Add(new(InputSeverity.Info, "Menu control is off",
                "The launcher isn't reading the controller for its own menus, so doubled presses there can't come from the launcher."));

        if (list.All(x => x.Severity is InputSeverity.Info))
            list.Insert(0, new(InputSeverity.Ok, "No settings problem found",
                "Nothing in the launcher's settings or the controllers Windows can see explains a doubled press. " +
                "If it still happens, the likely causes are the controller's own button contacts or another app. Note which button and which controller, and report it."));
        return list;
    }
}
