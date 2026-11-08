using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FitzLauncher.Core.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Storage;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace FitzLauncher.Windows;

public sealed record ControllerInfo(int Slot, string Kind, string Battery);

/// <summary>XInput access shared by detection and the remapper.</summary>
internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct State { public uint Packet; public Gamepad Pad; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vibration { public ushort Left, Right; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Capabilities { public byte Type, SubType; public ushort Flags; public Gamepad Gamepad; public Vibration Vibration; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Battery { public byte Type, Level; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] internal static extern int GetState(int index, out State state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] internal static extern int SetState(int index, ref Vibration v);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")] internal static extern int GetCapabilities(int index, int flags, out Capabilities c);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")] internal static extern int GetBattery(int index, byte devType, out Battery b);
}

/// <summary>Finds connected controllers: XInput slots (type, battery) and named game-controller devices.</summary>
public static class ControllerDevices
{
    public static List<ControllerInfo> XInputPads()
    {
        var list = new List<ControllerInfo>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                if (XInput.GetCapabilities(i, 0, out var caps) != 0) continue;
                var kind = caps.SubType switch { 1 => "Gamepad", 2 => "Wheel", 3 => "Arcade stick", 4 => "Flight stick", 5 => "Dance pad", 6 or 7 => "Guitar", 8 => "Drums", 0x13 => "Arcade pad", _ => "Controller" };
                var battery = XInput.GetBattery(i, 0, out var b) != 0 ? "" : b.Type switch
                {
                    1 => "wired",
                    0 => "",
                    _ => $"battery {b.Level switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" }}",
                };
                list.Add(new ControllerInfo(i + 1, kind, battery));
            }
        }
        catch (DllNotFoundException) { }
        return list;
    }

    /// <summary>Names of game controllers Windows knows about, including PlayStation/Switch pads (not XInput).</summary>
    public static List<string> DeviceNames()
    {
        var names = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Status='OK' AND (PNPClass='XnaComposite' OR PNPClass='XboxComposite' OR Name LIKE '%game controller%' OR Name LIKE '%Xbox%Controller%' " +
                "OR Name LIKE '%DualSense%' OR Name LIKE '%DUALSHOCK%' OR Name LIKE '%Wireless Controller%' OR Name LIKE '%Pro Controller%')");
            foreach (var o in s.Get())
                using (o)
                    if (o["Name"] is string n && !names.Contains(n)) names.Add(n);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>Raw state of the first connected controller (XInput, else a DualShock/DualSense being read), or null.</summary>
    public static (short lx, short ly, short rx, short ry, byte lt, byte rt)? ReadFirst()
    {
        try
        {
            for (var i = 0; i < 4; i++)
                if (XInput.GetState(i, out var st) == 0) return (st.Pad.LX, st.Pad.LY, st.Pad.RX, st.Pad.RY, st.Pad.LeftTrigger, st.Pad.RightTrigger);
        }
        catch (DllNotFoundException) { }
        if (PlayStationPads.Shared.Current is { } p) return (p.LX, p.LY, p.RX, p.RY, p.LT, p.RT);
        return null;
    }

    /// <summary>Full state of the first XInput pad (buttons in XInput bits), or null.</summary>
    public static Core.Input.PadSnapshot? ReadXInput()
    {
        try
        {
            for (var i = 0; i < 4; i++)
                if (XInput.GetState(i, out var st) == 0)
                    return new Core.Input.PadSnapshot(st.Pad.Buttons, st.Pad.LX, st.Pad.LY, st.Pad.RX, st.Pad.RY, st.Pad.LeftTrigger, st.Pad.RightTrigger);
        }
        catch (DllNotFoundException) { }
        return null;
    }

    public static bool HidHideInstalled() => HidHideClient.Find() is not null;

    public static bool ViGEmInstalled()
    {
        try { using var c = new ViGEmClient(); return true; }
        catch (Exception) { return false; } // VigemBusNotFoundException and friends
    }

    /// <summary>A short rumble to try a strength setting.</summary>
    public static async Task TestRumbleAsync(int strengthPercent)
    {
        try
        {
            var pads = XInputPads();
            if (pads.Count == 0) return;
            var v = new XInput.Vibration { Left = ControllerMath.Rumble(200, strengthPercent), Right = ControllerMath.Rumble(200, strengthPercent) };
            XInput.SetState(pads[0].Slot - 1, ref v);
            await Task.Delay(450);
            v = default;
            XInput.SetState(pads[0].Slot - 1, ref v);
        }
        catch (DllNotFoundException) { }
    }
}

/// <summary>
/// HidHide (Nefarius, free) hides the real controller from games so they only see the tuned virtual one;
/// the launcher registers itself so it can still read the real pad. Paths it hides are saved first and
/// unhidden afterwards, and at the next start if the launcher crashed.
/// </summary>
public sealed class HidHideClient(string cli)
{
    public static string? Find()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates = [Path.Combine(pf, "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe"), Path.Combine(pf, "Nefarius Software Solutions", "HidHide", "HidHideCLI.exe")];
        return candidates.FirstOrDefault(File.Exists);
    }

    private string Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(cli, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    /// <summary>Device instance paths of connected gaming devices (and their parent USB/Bluetooth device).</summary>
    public List<string> GamingDevicePaths()
    {
        var paths = new List<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonArray a: foreach (var x in a) Walk(x); break;
                case JsonObject o:
                    if (o["present"]?.GetValue<bool>() is false) return;
                    foreach (var key in new[] { "deviceInstancePath", "baseContainerDeviceInstancePath" })
                        if (o[key]?.GetValue<string>() is { Length: > 0 } p && !paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
                    foreach (var kv in o) if (kv.Value is JsonArray or JsonObject) Walk(kv.Value);
                    break;
            }
        }
        try { Walk(JsonNode.Parse(Run("--dev-gaming"))); }
        catch (System.Text.Json.JsonException) { }
        return paths;
    }

    public List<string> HiddenPaths()
    {
        try { return (JsonNode.Parse(Run("--dev-list")) as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? []; }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return []; }
    }

    public void RegisterApp(string exe) => Run($"--app-reg \"{exe}\"");
    public void Hide(string path) => Run($"--dev-hide \"{path}\"");
    public void Unhide(string path) => Run($"--dev-unhide \"{path}\"");
    public void CloakOn() => Run("--cloak-on");
}

/// <summary>
/// Applies a <see cref="ControllerProfile"/> while a game runs: reads the real controller, applies deadzones,
/// curves, trigger and button settings, and feeds a virtual Xbox 360 controller (ViGEmBus) that the game
/// uses, with the real one hidden by HidHide. Rumble from the game is scaled and passed to the real pad.
/// </summary>
public sealed class ControllerRemapper(ControllerProfile profile, AppPaths paths, Action<string> status) : IDisposable
{
    private string RestoreFile => Path.Combine(paths.Root, "controller-restore.json");
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private HidHideClient? _hide;
    private List<string> _hidden = [];
    private Thread? _thread;
    private volatile bool _run;
    private int _slot = -1;
    private bool _usesSony;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    public bool Start()
    {
        var pads = ControllerDevices.XInputPads();
        if (pads.Count > 0) _slot = pads[0].Slot - 1;
        else
        {
            // No Xbox-style pad: a DualShock/DualSense read directly over HID works too.
            PlayStationPads.Shared.Start();
            _usesSony = true;
            for (var i = 0; i < 20 && PlayStationPads.Shared.Current is null; i++) Thread.Sleep(50);
            if (PlayStationPads.Shared.Current is null)
            {
                status("Controller settings: no controller connected, so they're off this session.");
                PlayStationPads.Shared.Stop();
                _usesSony = false;
                return false;
            }
        }
        if (HidHideClient.Find() is not { } cli) { status("Controller settings need HidHide (free, by Nefarius) to hide the real controller from the game. They're off this session."); return false; }
        try { _client = new ViGEmClient(); }
        catch (Exception) { status("Controller settings need the ViGEmBus driver (free, by Nefarius). They're off this session."); return false; }

        _hide = new HidHideClient(cli);
        _hide.RegisterApp(Environment.ProcessPath!);
        var already = _hide.HiddenPaths();
        _hidden = _hide.GamingDevicePaths().Where(p => !already.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        Json.Save(RestoreFile, _hidden); // first, so a crash still unhides them at the next start
        foreach (var p in _hidden) _hide.Hide(p);
        _hide.CloakOn();

        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += OnFeedback;
        _pad.Connect();

        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Fitz controller" };
        _thread.Start();
        status("Controller settings on (deadzones, curves" + (profile.Vibration != 100 ? $", rumble {profile.Vibration}%" : "") + ")");
        return true;
    }

    private void Loop()
    {
        timeBeginPeriod(1);
        uint last = uint.MaxValue;
        try
        {
            while (_run)
            {
                if (_usesSony)
                {
                    var p = PlayStationPads.Shared.Current;
                    var g = p is { } v ? new XInput.Gamepad { Buttons = (ushort)(v.Buttons & ~Core.Input.PadSnapshot.Guide), LX = v.LX, LY = v.LY, RX = v.RX, RY = v.RY, LeftTrigger = v.LT, RightTrigger = v.RT } : default;
                    Submit(g);
                    Thread.Sleep(2);
                    continue;
                }
                if (XInput.GetState(_slot, out var s) == 0)
                {
                    if (s.Packet != last)
                    {
                        last = s.Packet;
                        Submit(s.Pad);
                    }
                }
                else if (last != 0) { last = 0; Submit(default); }
                Thread.Sleep(1);
            }
        }
        finally { timeEndPeriod(1); }
    }

    private static readonly (ushort mask, Xbox360Button button)[] ButtonMap =
    [
        (0x0001, Xbox360Button.Up), (0x0002, Xbox360Button.Down), (0x0004, Xbox360Button.Left), (0x0008, Xbox360Button.Right),
        (0x0010, Xbox360Button.Start), (0x0020, Xbox360Button.Back), (0x0040, Xbox360Button.LeftThumb), (0x0080, Xbox360Button.RightThumb),
        (0x0100, Xbox360Button.LeftShoulder), (0x0200, Xbox360Button.RightShoulder), (0x0400, Xbox360Button.Guide),
        (0x1000, Xbox360Button.A), (0x2000, Xbox360Button.B), (0x4000, Xbox360Button.X), (0x8000, Xbox360Button.Y),
    ];

    private void Submit(XInput.Gamepad g)
    {
        if (_pad is not { } pad) return;
        var buttons = ControllerMath.Buttons(g.Buttons, profile.SwapFaceButtons);
        foreach (var (mask, b) in ButtonMap) pad.SetButtonState(b, (buttons & mask) != 0);
        var (lx, ly) = ControllerMath.Stick(g.LX, g.LY, profile.LeftStick);
        var (rx, ry) = ControllerMath.Stick(g.RX, g.RY, profile.RightStick);
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, rx);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, ry);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, ControllerMath.Trigger(g.LeftTrigger, profile.TriggerDeadzone, profile.TriggerMax));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, ControllerMath.Trigger(g.RightTrigger, profile.TriggerDeadzone, profile.TriggerMax));
        pad.SubmitReport();
    }

    private void OnFeedback(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        var v = new XInput.Vibration { Left = ControllerMath.Rumble(e.LargeMotor, profile.Vibration), Right = ControllerMath.Rumble(e.SmallMotor, profile.Vibration) };
        if (_slot >= 0) XInput.SetState(_slot, ref v);
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(500);
        if (_slot >= 0) { var v = new XInput.Vibration(); XInput.SetState(_slot, ref v); }
        try { _pad?.Disconnect(); } catch (Exception) { /* already gone */ }
        _client?.Dispose();
        if (_usesSony) { PlayStationPads.Shared.Stop(); _usesSony = false; }
        if (_hide is not null) foreach (var p in _hidden) _hide.Unhide(p);
        try { File.Delete(RestoreFile); } catch (IOException) { }
    }

    /// <summary>At start-up: unhide controllers a crashed session left hidden.</summary>
    public static void RecoverIfNeeded(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, "controller-restore.json");
        if (Json.Load<List<string>>(file) is not { Count: > 0 } hidden || HidHideClient.Find() is not { } cli) return;
        var h = new HidHideClient(cli);
        foreach (var p in hidden) h.Unhide(p);
        try { File.Delete(file); } catch (IOException) { }
    }
}
