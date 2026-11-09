namespace FitzLauncher.Core.Input;

/// <summary>Windows actions a PS-button combo can run.</summary>
public enum PadAction
{
    None, VolumeUp, VolumeDown, Mute, BrightnessUp, BrightnessDown, PlayPause, NextTrack, PreviousTrack,
    Screenshot, GameBar, OnScreenKeyboard, TaskManager, ShowDesktop, SwitchWindow, OpenLauncher, TrainerOrGame, ToggleOverlay, Sleep, GameHelp, GameHelpAsk,
    TargetUp, TargetDown, PowerUp, PowerDown,
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
    public const int CurrentDefaults = 4;
    /// <summary>Fresh settings (not loaded from a file).</summary>
    public static PadComboSettings New() => new() { DefaultsVersion = CurrentDefaults };

    public bool Enabled { get; set; } = true;
    public List<PadCombo> Combos { get; set; } = Defaults();
    /// <summary>Show the actions wheel (with the shortcut legend) while PS is held on its own.</summary>
    public bool WheelEnabled { get; set; } = true;
    /// <summary>The eight wheel slots, clockwise from the top: flick the right stick to one and release PS.</summary>
    public List<PadAction> WheelActions { get; set; } = DefaultWheel();
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

    public const int WheelSlots = 8;

    public static List<PadAction> DefaultWheel() =>
    [
        PadAction.GameHelp,       // top
        PadAction.Screenshot,     // top right
        PadAction.ToggleOverlay,  // right
        PadAction.TargetUp,       // bottom right
        PadAction.Sleep,          // bottom
        PadAction.TargetDown,     // bottom left
        PadAction.TrainerOrGame,  // left
        PadAction.Mute,           // top left
    ];

    /// <summary>Slot names for settings and the wheel, clockwise from the top.</summary>
    public static readonly string[] SlotNames = ["Top", "Top right", "Right", "Bottom right", "Bottom", "Bottom left", "Left", "Top left"];

    /// <summary>The configured action for a slot (always <see cref="WheelSlots"/> entries; missing ones are empty).</summary>
    public PadAction WheelAction(int slot) => slot >= 0 && slot < WheelActions.Count ? WheelActions[slot] : PadAction.None;

    /// <summary>The wheel slot (0 = top, clockwise) a right-stick position points at, or -1 when it's near the centre.</summary>
    public static int SlotFor(short rx, short ry, int current = -1)
    {
        var mag = Math.Sqrt((double)rx * rx + (double)ry * ry);
        if (mag < 8000) return -1;                 // centred: nothing selected
        if (mag < 18000 && current >= 0) return current; // between the two thresholds, keep the selection (no flicker)
        if (mag < 18000) return -1;
        var deg = Math.Atan2(rx, ry) * 180 / Math.PI; // clockwise from the top (up = positive Y)
        return ((int)Math.Round(deg / 45.0) % WheelSlots + WheelSlots) % WheelSlots;
    }

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
            DefaultsVersion = 3;
        }
        if (WheelActions.Count != WheelSlots) WheelActions = DefaultWheel(); // v4: the actions wheel
        DefaultsVersion = CurrentDefaults;
        if (Combos.All(c => c.Action != PadAction.TrainerOrGame) && Combos.All(c => c.Button != PadSnapshot.RightThumb))
            Combos.Add(new PadCombo { Button = PadSnapshot.RightThumb, Action = PadAction.TrainerOrGame });
    }

    public PadAction ActionFor(ushort button) => Combos.FirstOrDefault(c => c.Button == button)?.Action ?? PadAction.None;
}

/// <summary>The actions wheel's state: whether it's showing and which slot is highlighted (-1 = none).</summary>
public readonly record struct WheelState(bool Open, int Slot);

/// <summary>
/// Turns PS-button input into events.
/// <list type="bullet">
/// <item>Tap PS on its own (released within 0.35 s) → <see cref="Tap"/> (opens OneXConsole).</item>
/// <item>Hold PS and press a button → that button's action (volume and brightness repeat while held).</item>
/// <item>Hold PS for 0.35 s without pressing anything → the actions wheel opens. Flick the right stick towards a
/// slot and release PS to run it; release with the stick centred to cancel. Pressing a shortcut button closes it.</item>
/// </list>
/// While PS is held the game only sees what it already had: buttons held when PS went down stay held until you
/// release them, new presses are kept for the shortcut, and the right stick is kept for the wheel (see <see cref="GameView"/>).
/// </summary>
public sealed class PadComboEngine(Func<PadComboSettings> settings)
{
    public const PadAction Tap = (PadAction)(-1);
    public static readonly TimeSpan WheelDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400), RepeatEvery = TimeSpan.FromMilliseconds(120);

    private readonly Lock _lock = new();
    private bool _psDown, _comboUsed, _wheelOpen;
    private DateTime _psSince;
    private ushort _held, _preHeld;
    private int _slot = -1;
    private readonly Dictionary<ushort, DateTime> _nextRepeat = [];

    /// <summary>True while PS is held.</summary>
    public bool Chording { get { lock (_lock) return _psDown; } }

    public WheelState Wheel { get { lock (_lock) return new WheelState(_wheelOpen, _wheelOpen ? _slot : -1); } }

    public static bool Repeats(PadAction a) => a is PadAction.VolumeUp or PadAction.VolumeDown or PadAction.BrightnessUp or PadAction.BrightnessDown;

    public List<PadAction> Update(PadSnapshot? pad, DateTime now)
    {
        lock (_lock)
        {
            var fired = new List<PadAction>();
            var s = pad ?? default;
            var ps = s.Has(PadSnapshot.Guide);
            var cfg = settings();
            var buttons = (ushort)(s.Buttons & ~PadSnapshot.Guide);

            if (ps && !_psDown)
            {
                _psDown = true; _psSince = now; _comboUsed = false; _wheelOpen = false; _slot = -1;
                _held = _preHeld = buttons; // what was already held stays the game's
            }
            else if (!ps && _psDown)
            {
                _psDown = false;
                _nextRepeat.Clear();
                if (_wheelOpen)
                {
                    if (_slot >= 0 && cfg.WheelAction(_slot) is var a and not PadAction.None) fired.Add(a); // release = run the highlighted slot
                }
                else if (!_comboUsed && now - _psSince < WheelDelay) fired.Add(Tap);
                _wheelOpen = false; _slot = -1; _preHeld = 0;
                return fired;
            }
            if (!_psDown) return fired;

            _preHeld &= buttons; // a pre-held button that's let go is no longer the game's

            // The wheel: after a short hold with nothing pressed.
            if (!_comboUsed && !_wheelOpen && cfg.Enabled && cfg.WheelEnabled && now - _psSince >= WheelDelay) _wheelOpen = true;
            if (_wheelOpen) _slot = PadComboSettings.SlotFor(s.RX, s.RY, _slot);

            var pressed = (ushort)(buttons & ~_held);
            foreach (var (bit, _) in PadComboSettings.Buttons)
            {
                if ((buttons & bit) == 0) { _nextRepeat.Remove(bit); continue; }
                var action = cfg.Enabled ? cfg.ActionFor(bit) : PadAction.None;
                if ((pressed & bit) != 0)
                {
                    _comboUsed = true; // any button with PS cancels the tap and the wheel, mapped or not
                    _wheelOpen = false; _slot = -1;
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

    /// <summary>
    /// What the game (or OneXConsole) should see right now. PS itself is never passed on. While PS is held only the
    /// buttons that were already down when PS was pressed stay down (so holding block or sprint doesn't drop out); new
    /// presses belong to the shortcut. With the wheel open the right stick is the wheel's, so it's centred for the game.
    /// Sticks and triggers otherwise pass through untouched.
    /// </summary>
    public PadSnapshot GameView(PadSnapshot s)
    {
        lock (_lock)
        {
            var clean = s with { Buttons = (ushort)(s.Buttons & ~PadSnapshot.Guide) };
            if (!_psDown || !settings().Enabled) return clean;
            clean = clean with { Buttons = (ushort)(clean.Buttons & _preHeld) };
            return _wheelOpen ? clean with { RX = 0, RY = 0 } : clean;
        }
    }
}
