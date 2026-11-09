using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using FitzLauncher.Core.Input;

namespace FitzLauncher.Windows;

/// <summary>Runs a <see cref="PadAction"/>: media/volume keys, brightness (WMI), Windows shortcuts.</summary>
public static class WindowsActions
{
    // Virtual-key codes: https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes
    private const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF, VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1,
        VK_MEDIA_PLAY_PAUSE = 0xB3, VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_TAB = 0x09, VK_ESCAPE = 0x1B,
        VK_SNAPSHOT = 0x2C, VK_G = 0x47, VK_D = 0x44, VK_A = 0x41;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    /// <summary>Called for <see cref="PadAction.OpenLauncher"/> (set by the app).</summary>
    public static Action? OpenLauncher { get; set; }

    /// <summary>The running trainer and game processes, for <see cref="PadAction.TrainerOrGame"/> (set by the app).</summary>
    public static Func<(int? Trainer, IReadOnlyCollection<int> Game)>? TrainerAndGame { get; set; }

    /// <summary>In-game FPS target step (+1 / -1) and power step, for the running game (set by the app per session).</summary>
    public static Action<int>? TargetStep { get; set; }
    public static Action<int>? PowerStep { get; set; }

    /// <summary>Called for <see cref="PadAction.ToggleOverlay"/> (set by the app).</summary>
    public static Action? ToggleOverlay { get; set; }

    // https://learn.microsoft.com/windows/win32/api/powrprof/nf-powrprof-setsuspendstate
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    public static void Run(PadAction action)
    {
        switch (action)
        {
            case PadAction.VolumeUp: Keys(VK_VOLUME_UP); break;
            case PadAction.VolumeDown: Keys(VK_VOLUME_DOWN); break;
            case PadAction.Mute: Keys(VK_VOLUME_MUTE); break;
            case PadAction.PlayPause: Keys(VK_MEDIA_PLAY_PAUSE); break;
            case PadAction.NextTrack: Keys(VK_MEDIA_NEXT); break;
            case PadAction.PreviousTrack: Keys(VK_MEDIA_PREV); break;
            case PadAction.Screenshot: Keys(VK_LWIN, VK_SNAPSHOT); break;       // saved to Pictures\Screenshots
            case PadAction.GameBar: Keys(VK_LWIN, VK_G); break;
            case PadAction.ShowDesktop: Keys(VK_LWIN, VK_D); break;
            case PadAction.TaskManager: Keys(VK_CONTROL, VK_SHIFT, VK_ESCAPE); break;
            case PadAction.SwitchWindow: AltTab(back: false); break;
            case PadAction.SwitchWindowBack: AltTab(back: true); break;
            case PadAction.BrightnessUp: Brightness(+10); break;
            case PadAction.BrightnessDown: Brightness(-10); break;
            case PadAction.OnScreenKeyboard: TouchKeyboard(); break;
            case PadAction.OpenLauncher: OpenLauncher?.Invoke(); break;
            case PadAction.TrainerOrGame: SwitchTrainerAndGame(); break;
            case PadAction.ToggleOverlay: ToggleOverlay?.Invoke(); break;
            case PadAction.TargetUp: TargetStep?.Invoke(+1); break;
            case PadAction.TargetDown: TargetStep?.Invoke(-1); break;
            case PadAction.PowerUp: PowerStep?.Invoke(+1); break;
            case PadAction.PowerDown: PowerStep?.Invoke(-1); break;
            // AI Game Companion's fixed global hotkeys: Ctrl+Shift+G panel, Ctrl+Shift+A screenshot + question.
            case PadAction.GameHelp: Keys(VK_CONTROL, VK_SHIFT, VK_G); break;
            case PadAction.GameHelpAsk: Keys(VK_CONTROL, VK_SHIFT, VK_A); break;
            case PadAction.Sleep:
                // Same as Start → Sleep (Modern Standby on handhelds). A moment's pause so the button release
                // doesn't count as wake-up input.
                Thread.Sleep(1200);
                SetSuspendState(false, false, false);
                break;
        }
    }

    /// <summary>Trainer in front → back to the game; otherwise bring the trainer forward.</summary>
    private static void SwitchTrainerAndGame()
    {
        if (TrainerAndGame?.Invoke() is not { Trainer: { } trainer } ctx) return;
        if (ForegroundWindow.IsForeground([trainer])) ForegroundWindow.Activate(ForegroundWindow.Find(ctx.Game));
        else ForegroundWindow.Activate(ForegroundWindow.Find([trainer]));
    }

    private static readonly AltTabHold s_altTab = new();
    private static readonly Lock s_altLock = new();
    private static int s_exitHooked;

    /// <summary>True while Alt is being held for window switching (PS still held).</summary>
    public static bool AltTabActive { get { lock (s_altLock) return s_altTab.Held; } }

    /// <summary>
    /// Like holding Alt on a keyboard: the first press holds Alt and taps Tab (the switcher opens), each further press taps
    /// Tab again to move along, and <see cref="EndAltTab"/> (PS let go) releases Alt, which picks the highlighted window.
    /// </summary>
    private static void AltTab(bool back)
    {
        lock (s_altLock)
        {
            if (!s_altTab.Held && Interlocked.Exchange(ref s_exitHooked, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => EndAltTab(); // never leave Alt stuck down
            var first = !s_altTab.Held;
            var steps = s_altTab.Press(back);
            KeyInjector.Send(steps.Take(first ? 1 : 0));
            if (first) Thread.Sleep(30); // let Windows see Alt before Tab
            KeyInjector.Send(steps.Skip(first ? 1 : 0));
        }
    }

    /// <summary>Lets go of Alt if window switching is in progress (switches to the highlighted window).</summary>
    public static void EndAltTab()
    {
        lock (s_altLock) KeyInjector.Send(s_altTab.Release());
    }

    /// <summary>Presses the keys in order and releases them in reverse (a chord).</summary>
    private static void Keys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, 0);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>Built-in screen brightness via WMI (laptops/handhelds; external monitors don't support it).</summary>
    private static void Brightness(int delta)
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            int current;
            using (var q = new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightness")))
            {
                var first = q.Get().Cast<ManagementObject>().FirstOrDefault();
                if (first is null) return;
                current = Convert.ToInt32(first["CurrentBrightness"]);
            }
            var next = (byte)Math.Clamp(current + delta, 0, 100);
            using var m = new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightnessMethods"));
            foreach (ManagementObject o in m.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", [1u, next]);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException) { }
    }

    private static void TouchKeyboard()
    {
        var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
        try { Process.Start(new ProcessStartInfo(File.Exists(tabTip) ? tabTip : "osk.exe") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
