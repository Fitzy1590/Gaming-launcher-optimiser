namespace FitzLauncher.Core.Input;

/// <summary>Windows actions a PS-button combo can run.</summary>
public enum PadAction
{
    None, VolumeUp, VolumeDown, Mute, BrightnessUp, BrightnessDown, PlayPause, NextTrack, PreviousTrack,
    Screenshot, GameBar, OnScreenKeyboard, TaskManager, ShowDesktop, SwitchWindow, OpenLauncher, TrainerOrGame, ToggleOverlay, Sleep, GameHelp, GameHelpAsk,
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
    public const int CurrentDefaults = 3;
    /// <summary>Fresh settings (not loaded from a file).</summary>
    public static PadComboSettings New() => new() { DefaultsVersion = CurrentDefaults };

    public bool Enabled { get; set; } = true;
    public List<PadCombo> Combos { get; set; } = Defaults();
    /// <summary>Version of the built-in defaults these settings were created from (see <see cref="AddNewDefaults"/>).</summary>
    public int DefaultsVersion { get; set; } // 0 in settings saved before versioning; new settings get the current version from New()

    public static List<PadCombo> Defaults() =>
    [
        new() { Button = PadSnapshot.Up, Action = PadAction.VolumeUp },
        new() { Button = PadSnapshot.Down, Action = PadAction.VolumeDown },
        new() { Button = PadSnapshot.Left, Action = PadAction.Sleep },
        new() { Button = PadSnapshot.Right, Action = PadAction.ToggleOverlay },
        new() { Button = PadSnapshot.A, Action = PadAction.Mute },
        new() { Button = PadSnapshot.B, Action = PadAction.SwitchWindow },
        new() { Button = PadSnapshot.X, Action = PadAction.GameBar },
        new() { Button = PadSnapshot.Y, Action = PadAction.GameHelp },
        new() { Button = PadSnapshot.LeftThumb, Action = PadAction.OnScreenKeyboard },
        new() { Button = PadSnapshot.LeftShoulder, Action = PadAction.BrightnessDown },
        new() { Button = PadSnapshot.RightShoulder, Action = PadAction.BrightnessUp },
        new() { Button = PadSnapshot.Back, Action = PadAction.Screenshot },
        new() { Button = PadSnapshot.Start, Action = PadAction.PlayPause },
        new() { Button = PadSnapshot.RightThumb, Action = PadAction.TrainerOrGame },
    ];

    /// <summary>Every button that can be combined with PS, with its DualShock name.</summary>
    public static readonly (ushort Button, string Name)[] Buttons =
    [
        (PadSnapshot.Up, "D-pad up"), (PadSnapshot.Down, "D-pad down"), (PadSnapshot.Left, "D-pad left"), (PadSnapshot.Right, "D-pad right"),
        (PadSnapshot.A, "Cross"), (PadSnapshot.B, "Circle"), (PadSnapshot.X, "Square"), (PadSnapshot.Y, "Triangle"),
        (PadSnapshot.LeftShoulder, "L1"), (PadSnapshot.RightShoulder, "R1"), (PadSnapshot.Back, "Share / Create"), (PadSnapshot.Start, "Options"),
        (PadSnapshot.LeftThumb, "L3"), (PadSnapshot.RightThumb, "R3"),
    ];

    /// <summary>Adds combos introduced in later versions to saved settings, without touching the user's choices.</summary>
    public void AddNewDefaults()
    {
        // v2: PS + Left sleeps, PS + Right hides the FPS counters; brightness moved to L1/R1. Only applied when
        // those four buttons are still on the v1 defaults, so nobody's own choices are overwritten.
        if (DefaultsVersion < 2)
        {
            bool Is(ushort b, PadAction a) => ActionFor(b) == a;
            if (Is(PadSnapshot.Left, PadAction.BrightnessDown) && Is(PadSnapshot.Right, PadAction.BrightnessUp)
                && Is(PadSnapshot.LeftShoulder, PadAction.PreviousTrack) && Is(PadSnapshot.RightShoulder, PadAction.NextTrack))
            {
                Combos.RemoveAll(c => c.Button is PadSnapshot.Left or PadSnapshot.Right or PadSnapshot.LeftShoulder or PadSnapshot.RightShoulder);
                Combos.Add(new PadCombo { Button = PadSnapshot.Left, Action = PadAction.Sleep });
                Combos.Add(new PadCombo { Button = PadSnapshot.Right, Action = PadAction.ToggleOverlay });
                Combos.Add(new PadCombo { Button = PadSnapshot.LeftShoulder, Action = PadAction.BrightnessDown });
                Combos.Add(new PadCombo { Button = PadSnapshot.RightShoulder, Action = PadAction.BrightnessUp });
            }
            DefaultsVersion = 2;
        }
        // v3: PS + Triangle opens game help (AI Game Companion); the on-screen keyboard moves to PS + L3.
        if (DefaultsVersion < 3)
        {
            if (ActionFor(PadSnapshot.Y) == PadAction.OnScreenKeyboard && ActionFor(PadSnapshot.LeftThumb) == PadAction.None)
            {
                Combos.RemoveAll(c => c.Button == PadSnapshot.Y);
                Combos.Add(new PadCombo { Button = PadSnapshot.Y, Action = PadAction.GameHelp });
                Combos.Add(new PadCombo { Button = PadSnapshot.LeftThumb, Action = PadAction.OnScreenKeyboard });
            }
            DefaultsVersion = CurrentDefaults;
        }
        if (Combos.All(c => c.Action != PadAction.TrainerOrGame) && Combos.All(c => c.Button != PadSnapshot.RightThumb))
            Combos.Add(new PadCombo { Button = PadSnapshot.RightThumb, Action = PadAction.TrainerOrGame });
    }

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
