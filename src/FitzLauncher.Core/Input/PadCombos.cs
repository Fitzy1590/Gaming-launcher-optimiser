namespace FitzLauncher.Core.Input;

/// <summary>Windows actions a PS-button combo can run.</summary>
public enum PadAction
{
    None, VolumeUp, VolumeDown, Mute, BrightnessUp, BrightnessDown, PlayPause, NextTrack, PreviousTrack,
    Screenshot, GameBar, OnScreenKeyboard, TaskManager, ShowDesktop, SwitchWindow, OpenLauncher,
}

/// <summary>One PS + button combo.</summary>
public sealed class PadCombo
{
    /// <summary>The button pressed while PS is held (XInput bit, see <see cref="PadSnapshot"/>).</summary>
    public ushort Button { get; set; }
    public PadAction Action { get; set; }
}

public sealed class PadComboSettings
{
    public bool Enabled { get; set; } = true;
    public List<PadCombo> Combos { get; set; } = Defaults();

    public static List<PadCombo> Defaults() =>
    [
        new() { Button = PadSnapshot.Up, Action = PadAction.VolumeUp },
        new() { Button = PadSnapshot.Down, Action = PadAction.VolumeDown },
        new() { Button = PadSnapshot.Left, Action = PadAction.BrightnessDown },
        new() { Button = PadSnapshot.Right, Action = PadAction.BrightnessUp },
        new() { Button = PadSnapshot.A, Action = PadAction.Mute },
        new() { Button = PadSnapshot.B, Action = PadAction.SwitchWindow },
        new() { Button = PadSnapshot.X, Action = PadAction.GameBar },
        new() { Button = PadSnapshot.Y, Action = PadAction.OnScreenKeyboard },
        new() { Button = PadSnapshot.LeftShoulder, Action = PadAction.PreviousTrack },
        new() { Button = PadSnapshot.RightShoulder, Action = PadAction.NextTrack },
        new() { Button = PadSnapshot.Back, Action = PadAction.Screenshot },
        new() { Button = PadSnapshot.Start, Action = PadAction.PlayPause },
    ];

    /// <summary>Every button that can be combined with PS, with its DualShock name.</summary>
    public static readonly (ushort Button, string Name)[] Buttons =
    [
        (PadSnapshot.Up, "D-pad up"), (PadSnapshot.Down, "D-pad down"), (PadSnapshot.Left, "D-pad left"), (PadSnapshot.Right, "D-pad right"),
        (PadSnapshot.A, "Cross"), (PadSnapshot.B, "Circle"), (PadSnapshot.X, "Square"), (PadSnapshot.Y, "Triangle"),
        (PadSnapshot.LeftShoulder, "L1"), (PadSnapshot.RightShoulder, "R1"), (PadSnapshot.Back, "Share / Create"), (PadSnapshot.Start, "Options"),
        (PadSnapshot.LeftThumb, "L3"), (PadSnapshot.RightThumb, "R3"),
    ];

    public PadAction ActionFor(ushort button) => Combos.FirstOrDefault(c => c.Button == button)?.Action ?? PadAction.None;
}

/// <summary>
/// Turns PS-button input into events: hold PS and press a button → that button's action (volume and brightness
/// repeat while held); tap PS on its own → <see cref="Tap"/> (opens OneXConsole). A combo cancels the tap.
/// </summary>
public sealed class PadComboEngine(Func<PadComboSettings> settings)
{
    public const PadAction Tap = (PadAction)(-1);
    private static readonly TimeSpan TapMax = TimeSpan.FromMilliseconds(700), RepeatDelay = TimeSpan.FromMilliseconds(400), RepeatEvery = TimeSpan.FromMilliseconds(120);

    private bool _psDown, _comboUsed;
    private DateTime _psSince;
    private ushort _held;
    private readonly Dictionary<ushort, DateTime> _nextRepeat = [];

    /// <summary>True while PS is held: the other buttons belong to the combo, so callers hide them from games/menus.</summary>
    public bool Chording => _psDown;

    public static bool Repeats(PadAction a) => a is PadAction.VolumeUp or PadAction.VolumeDown or PadAction.BrightnessUp or PadAction.BrightnessDown;

    public List<PadAction> Update(PadSnapshot? pad, DateTime now)
    {
        var fired = new List<PadAction>();
        var s = pad ?? default;
        var ps = s.Has(PadSnapshot.Guide);
        var cfg = settings();

        if (ps && !_psDown) { _psDown = true; _psSince = now; _comboUsed = false; _held = (ushort)(s.Buttons & ~PadSnapshot.Guide); }
        else if (!ps && _psDown)
        {
            _psDown = false;
            _nextRepeat.Clear();
            if (!_comboUsed && now - _psSince <= TapMax) fired.Add(Tap);
            return fired;
        }
        if (!_psDown) return fired;

        var buttons = (ushort)(s.Buttons & ~PadSnapshot.Guide);
        var pressed = (ushort)(buttons & ~_held);
        foreach (var (bit, _) in PadComboSettings.Buttons)
        {
            if ((buttons & bit) == 0) { _nextRepeat.Remove(bit); continue; }
            var action = cfg.Enabled ? cfg.ActionFor(bit) : PadAction.None;
            if ((pressed & bit) != 0)
            {
                _comboUsed = true; // any button with PS cancels the tap, mapped or not
                if (action == PadAction.None) continue;
                fired.Add(action);
                if (Repeats(action)) _nextRepeat[bit] = now + RepeatDelay;
            }
            else if (_nextRepeat.TryGetValue(bit, out var due) && now >= due)
            {
                fired.Add(action);
                _nextRepeat[bit] = now + RepeatEvery;
            }
        }
        _held = buttons;
        return fired;
    }
}
