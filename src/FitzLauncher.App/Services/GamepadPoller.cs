using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FitzLauncher.App.Services;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, LeftShoulder, RightShoulder, Start, Back, ScrollUp, ScrollDown }

/// <summary>
/// Controller input for the menus: Xbox-style pads via XInput, plus DualShock 4 / DualSense read directly over
/// HID (no DS4Windows or Steam needed). Polled at 60 Hz with key-repeat when a direction is held; the left stick
/// behaves like the D-pad, the right stick scrolls.
/// </summary>
public sealed class GamepadPoller : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public XInputGamepad Pad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState(int index, out XInputState state);

    private static readonly (ushort mask, PadButton button)[] Map =
    [
        (0x0001, PadButton.Up), (0x0002, PadButton.Down), (0x0004, PadButton.Left), (0x0008, PadButton.Right),
        (0x0010, PadButton.Start), (0x0020, PadButton.Back), (0x0100, PadButton.LeftShoulder), (0x0200, PadButton.RightShoulder),
        (0x1000, PadButton.A), (0x2000, PadButton.B), (0x4000, PadButton.X), (0x8000, PadButton.Y),
    ];

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<PadButton, DateTime> _held = [];
    private bool _available = true;

    public event Action<PadButton>? Pressed;

    public GamepadPoller()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    private bool _sonyStarted;

    public void Start()
    {
        if (!_sonyStarted) { FitzLauncher.Windows.PlayStationPads.Shared.Start(); _sonyStarted = true; }
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_sonyStarted) { FitzLauncher.Windows.PlayStationPads.Shared.Stop(); _sonyStarted = false; }
    }

    private void Poll()
    {
        var down = new HashSet<PadButton>();
        if (_available)
        {
            XInputState state = default;
            var connected = false;
            try
            {
                for (var i = 0; i < 4 && !connected; i++) connected = GetState(i, out state) == 0;
            }
            catch (DllNotFoundException) { _available = false; }
            if (connected) Add(down, state.Pad.Buttons, state.Pad.LX, state.Pad.LY, state.Pad.RY);
        }
        // DualShock / DualSense (merged, so a pad seen both ways doesn't double-press).
        if (FitzLauncher.Windows.PlayStationPads.Shared.Current is { } ps) Add(down, ps.Buttons, ps.LX, ps.LY, ps.RY);

        var now = DateTime.UtcNow;
        foreach (var b in down)
        {
            if (!_held.TryGetValue(b, out var next))
            {
                Pressed?.Invoke(b);
                _held[b] = now.AddMilliseconds(380); // first repeat delay
            }
            else if (now >= next && b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right or PadButton.ScrollUp or PadButton.ScrollDown)
            {
                Pressed?.Invoke(b);
                _held[b] = now.AddMilliseconds(110);
            }
        }
        foreach (var b in _held.Keys.Where(k => !down.Contains(k)).ToList()) _held.Remove(b);
    }

    private static void Add(HashSet<PadButton> down, ushort buttons, short lx, short ly, short ry)
    {
        foreach (var (mask, b) in Map) if ((buttons & mask) != 0) down.Add(b);
        const short dead = 16000;
        if (lx < -dead) down.Add(PadButton.Left);
        if (lx > dead) down.Add(PadButton.Right);
        if (ly > dead) down.Add(PadButton.Up);
        if (ly < -dead) down.Add(PadButton.Down);
        if (ry > dead) down.Add(PadButton.ScrollUp);
        if (ry < -dead) down.Add(PadButton.ScrollDown);
    }

    public void Dispose() => Stop();
}
