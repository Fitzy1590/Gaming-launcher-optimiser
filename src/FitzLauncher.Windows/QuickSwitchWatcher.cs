using System.Runtime.InteropServices;

namespace FitzLauncher.Windows;

/// <summary>
/// In-game FPS target switching with the controller: hold View (Back) and press D-pad up or down.
/// XInput works whatever window has focus, so this runs during play at a light 30 Hz poll and fires
/// once per press. (Keyboard: Ctrl+Alt+Up/Down via global hotkeys in the app.)
/// </summary>
public sealed class QuickSwitchWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public XInputGamepad Pad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState(int index, out XInputState state);

    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, Back = 0x0020;
    private readonly Timer _timer;
    private ushort _last;
    private bool _disabled;

    /// <summary>+1 for up, -1 for down.</summary>
    public event Action<int>? Step;

    public QuickSwitchWatcher() => _timer = new Timer(_ => Poll(), null, 0, 33);

    private void Poll()
    {
        if (_disabled) return;
        ushort buttons = 0;
        try
        {
            for (var i = 0; i < 4; i++)
                if (GetState(i, out var s) == 0) { buttons = s.Pad.Buttons; break; }
        }
        catch (DllNotFoundException) { _disabled = true; return; }

        var pressed = (ushort)(buttons & ~_last);
        _last = buttons;
        if ((buttons & Back) == 0) return;
        if ((pressed & DpadUp) != 0) Step?.Invoke(+1);
        else if ((pressed & DpadDown) != 0) Step?.Invoke(-1);
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Sends a folder to the Recycle Bin (Windows shows its own progress and can undo).</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    public static bool Delete(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_WANTNUKEWARNING = 0x4000;
        var op = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING, // warns if too big for the bin
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
