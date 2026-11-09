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
        VK_SNAPSHOT = 0x2C, VK_G = 0x47, VK_D = 0x44;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    /// <summary>Called for <see cref="PadAction.OpenLauncher"/> (set by the app).</summary>
    public static Action? OpenLauncher { get; set; }

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
            case PadAction.SwitchWindow: Keys(VK_MENU, VK_TAB); break;
            case PadAction.BrightnessUp: Brightness(+10); break;
            case PadAction.BrightnessDown: Brightness(-10); break;
            case PadAction.OnScreenKeyboard: TouchKeyboard(); break;
            case PadAction.OpenLauncher: OpenLauncher?.Invoke(); break;
        }
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
