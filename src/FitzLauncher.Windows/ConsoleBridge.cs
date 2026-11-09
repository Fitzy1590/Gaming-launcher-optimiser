using System.Diagnostics;
using System.Runtime.InteropServices;
using FitzLauncher.Core.Input;
using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace FitzLauncher.Windows;

/// <summary>
/// The PS button on a DualShock/DualSense opens OneXConsole (OneXPlayer's console app), like the handheld's own
/// console button: press again to go back to the game. While OneXConsole is in front, the Sony pad is mirrored to
/// a virtual Xbox controller (ViGEmBus) so OneXConsole can be driven with it; without ViGEmBus the D-pad, Cross
/// and Circle are sent as arrow keys, Enter and Esc. Runs during games too.
/// </summary>
public sealed class ConsoleBridge : IDisposable
{
    private readonly Func<string?> _pathOverride;
    private readonly Func<bool> _mirrorEnabled;
    private readonly Action<string> _log;
    private Thread? _thread;
    private volatile bool _run;
    private nint _returnTo;

    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private bool _viGEmMissing;
    private ushort _lastKeys;

    private readonly Func<bool> _consoleEnabled;
    private readonly PadComboEngine _combos;

    /// <summary>True while PS is held (its combo buttons shouldn't reach games or menus).</summary>
    public static bool Chording { get; private set; }

    public ConsoleBridge(Func<string?> pathOverride, Func<bool> mirrorEnabled, Action<string> log, Func<bool>? consoleEnabled = null, Func<PadComboSettings>? combos = null)
    {
        _pathOverride = pathOverride;
        _mirrorEnabled = mirrorEnabled;
        _log = log;
        _consoleEnabled = consoleEnabled ?? (() => true);
        var c = combos ?? (() => new PadComboSettings { Enabled = false });
        _combos = new PadComboEngine(c);
    }

    public bool Running => _run;

    public void Start()
    {
        if (_run) return;
        _run = true;
        PlayStationPads.Shared.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Console button", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_run) return;
        _run = false;
        _thread?.Join(500);
        DisconnectPad();
        PlayStationPads.Shared.Stop();
    }

    private void Loop()
    {
        var lastForegroundCheck = DateTime.MinValue;
        var consoleInFront = false;
        while (_run)
        {
            var s = PlayStationPads.Shared.Current;
            // PS tap → OneXConsole; PS held + a button → that button's Windows action.
            foreach (var action in _combos.Update(s, DateTime.UtcNow))
            {
                try
                {
                    if (action == PadComboEngine.Tap) { if (_consoleEnabled()) Toggle(); }
                    else WindowsActions.Run(action);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { _log($"PS button ({action}): " + e.Message); }
            }
            Chording = _combos.Chording;
            if (Chording && s is { } held) s = held with { Buttons = 0 }; // the combo isn't also a press in OneXConsole

            if (DateTime.UtcNow - lastForegroundCheck > TimeSpan.FromMilliseconds(250))
            {
                lastForegroundCheck = DateTime.UtcNow;
                consoleInFront = ConsoleWindowInFront(out _);
            }
            if (consoleInFront && _mirrorEnabled() && s is { } pad) Mirror(pad);
            else if (_pad is not null || _lastKeys != 0) { DisconnectPad(); _lastKeys = 0; }
            Thread.Sleep(consoleInFront ? 4 : 16);
        }
        Chording = false;
    }

    // ------------------------------------------------------------------ open / go back

    /// <summary>Opens (or brings forward) OneXConsole; if it's already in front, returns to the previous window.</summary>
    public void Toggle()
    {
        if (ConsoleWindowInFront(out var hwnd))
        {
            if (_returnTo != 0 && IsWindow(_returnTo)) Activate(_returnTo);
            else ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
            return;
        }
        _returnTo = GetForegroundWindow();
        if (FindConsoleWindow() is { } existing && existing != 0)
        {
            if (IsIconic(existing)) ShowWindow(existing, 9 /* SW_RESTORE */);
            Activate(existing);
            return;
        }
        var path = Locate(_pathOverride());
        if (path is null) { _log("PS button: OneXConsole wasn't found. Set its location in Settings → Controllers."); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        // Bring it forward once its window appears (it may open behind a game otherwise).
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            if (FindConsoleWindow() is { } w && w != 0) { Activate(w); break; }
        }
    }

    private static bool IsConsoleProcess(string name) =>
        name.StartsWith("OneXConsole", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OneX Console", StringComparison.OrdinalIgnoreCase);

    private bool ConsoleWindowInFront(out nint hwnd)
    {
        hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return IsConsoleProcess(p.ProcessName) || OverrideName() is { } n && p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private string? OverrideName() =>
        _pathOverride() is { Length: > 0 } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(p) : null;

    private nint? FindConsoleWindow()
    {
        var name = OverrideName();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if ((IsConsoleProcess(p.ProcessName) || name is not null && p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) && p.MainWindowHandle != 0)
                        return p.MainWindowHandle;
                }
                catch (InvalidOperationException) { }
            }
        }
        return null;
    }

    /// <summary>
    /// Where OneXConsole is: the path set in Settings, a running copy, the uninstall entry, the usual install
    /// folders, or its Start menu shortcut. Null when not found.
    /// </summary>
    public static string? Locate(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (IsConsoleProcess(p.ProcessName) && p.MainModule?.FileName is { } f && File.Exists(f)) return f; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = root.OpenSubKey(keyPath);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                var name = app?.GetValue("DisplayName") as string ?? "";
                if (!name.Replace(" ", "").Contains("OneXConsole", StringComparison.OrdinalIgnoreCase)) continue;
                if (app?.GetValue("DisplayIcon") is string icon && ExeFromIcon(icon) is { } exe) return exe;
                if (app?.GetValue("InstallLocation") is string dir && Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir, "OneX*Console*.exe", SearchOption.AllDirectories).FirstOrDefault() is { } found) return found;
            }
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var c in new[] { Path.Combine(pf, "OneXConsole", "OneXConsole.exe"), Path.Combine(pf86, "OneXConsole", "OneXConsole.exe"), Path.Combine(pf, "ONEXPLAYER", "OneXConsole", "OneXConsole.exe"), Path.Combine(pf86, "ONEXPLAYER", "OneXConsole", "OneXConsole.exe") })
            if (File.Exists(c)) return c;
        foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        {
            try
            {
                if (Directory.Exists(menu) && Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Replace(" ", "").Contains("OneXConsole", StringComparison.OrdinalIgnoreCase)) is { } lnk)
                    return lnk;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string? ExeFromIcon(string icon)
    {
        var path = icon.Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 2 && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) path = path[..comma].Trim('"');
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    // ------------------------------------------------------------------ driving OneXConsole with the Sony pad

    private static readonly (ushort mask, Xbox360Button button)[] ButtonMap =
    [
        (PadSnapshot.Up, Xbox360Button.Up), (PadSnapshot.Down, Xbox360Button.Down), (PadSnapshot.Left, Xbox360Button.Left), (PadSnapshot.Right, Xbox360Button.Right),
        (PadSnapshot.Start, Xbox360Button.Start), (PadSnapshot.Back, Xbox360Button.Back), (PadSnapshot.LeftThumb, Xbox360Button.LeftThumb), (PadSnapshot.RightThumb, Xbox360Button.RightThumb),
        (PadSnapshot.LeftShoulder, Xbox360Button.LeftShoulder), (PadSnapshot.RightShoulder, Xbox360Button.RightShoulder),
        (PadSnapshot.A, Xbox360Button.A), (PadSnapshot.B, Xbox360Button.B), (PadSnapshot.X, Xbox360Button.X), (PadSnapshot.Y, Xbox360Button.Y),
    ];

    private void Mirror(PadSnapshot s)
    {
        if (_pad is null && !_viGEmMissing)
        {
            try
            {
                _client = new ViGEmClient();
                _pad = _client.CreateXbox360Controller();
                _pad.AutoSubmitReport = false;
                _pad.Connect();
            }
            catch (Exception)
            {
                _viGEmMissing = true; // keyboard fallback from now on
                _client?.Dispose();
                _client = null;
                _pad = null;
            }
        }
        if (_pad is { } pad)
        {
            foreach (var (mask, b) in ButtonMap) pad.SetButtonState(b, s.Has(mask));
            pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LX);
            pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LY);
            pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RX);
            pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RY);
            pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LT);
            pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RT);
            pad.SubmitReport();
            return;
        }
        // Keyboard fallback: arrows, Enter, Esc, Tab on the press of each button.
        var keys = (ushort)(s.Buttons & (PadSnapshot.Up | PadSnapshot.Down | PadSnapshot.Left | PadSnapshot.Right | PadSnapshot.A | PadSnapshot.B | PadSnapshot.RightShoulder));
        var pressed = (ushort)(keys & ~_lastKeys);
        _lastKeys = keys;
        foreach (var (mask, vk) in new[] { (PadSnapshot.Up, (byte)0x26), (PadSnapshot.Down, (byte)0x28), (PadSnapshot.Left, (byte)0x25), (PadSnapshot.Right, (byte)0x27), (PadSnapshot.A, (byte)0x0D), (PadSnapshot.B, (byte)0x1B), (PadSnapshot.RightShoulder, (byte)0x09) })
            if ((pressed & mask) != 0) { keybd_event(vk, 0, 0, 0); keybd_event(vk, 0, 2, 0); }
    }

    private void DisconnectPad()
    {
        try { _pad?.Disconnect(); } catch (Exception) { /* already gone */ }
        _pad = null;
        _client?.Dispose();
        _client = null;
    }

    // ------------------------------------------------------------------ win32

    private static void Activate(nint hwnd) => ForegroundWindow.Activate(hwnd);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    public void Dispose() => Stop();
}
