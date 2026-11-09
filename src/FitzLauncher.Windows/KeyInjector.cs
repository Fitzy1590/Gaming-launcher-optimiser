using System.Runtime.InteropServices;
using FitzLauncher.Core.Input;

namespace FitzLauncher.Windows;

/// <summary>Injects keyboard events (shortcuts, trainer hotkeys) as the system keyboard would send them.</summary>
public static class KeyInjector
{
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-keybd_event and MapVirtualKeyW (MAPVK_VK_TO_VSC = 0)
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>Sends the steps back to back.</summary>
    public static void Send(IEnumerable<KeyStep> steps)
    {
        foreach (var s in steps) One(s);
    }

    /// <summary>Sends the steps with a short pause between them (some programs poll the keyboard and miss instant taps).</summary>
    public static async Task SendAsync(IEnumerable<KeyStep> steps, int gapMs, CancellationToken ct = default)
    {
        foreach (var s in steps)
        {
            One(s);
            await Task.Delay(gapMs, ct).ConfigureAwait(false);
        }
    }

    private static void One(KeyStep s)
    {
        var flags = (s.Down ? 0u : KEYEVENTF_KEYUP) | (Vk.IsExtended(s.Vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        keybd_event(s.Vk, (byte)MapVirtualKeyW(s.Vk, 0), flags, 0);
    }
}
