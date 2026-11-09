using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

/// <summary>
/// Editor for a controller profile, with a live preview: the dot shows where the stick physically is, the
/// ring where the game will see it after deadzone, curve and sensitivity.
/// </summary>
public sealed partial class ControllerProfileViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    // Saving writes the whole library file; while a slider is dragged that would run on every pixel and make
    // the drag stick, so changes are saved once the value settles.
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public ControllerProfile Profile { get; }

    public ControllerProfileViewModel(ControllerProfile profile, Action save)
    {
        Profile = profile;
        _save = save;
        _timer.Tick += (_, _) => Poll();
        _saveTimer.Tick += (_, _) => Flush();
    }

    /// <summary>Writes any pending change now (also called when the editor closes).</summary>
    public void Flush()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        _save();
    }

    public static IReadOnlyList<StickCurve> Curves { get; } = Enum.GetValues<StickCurve>();

    private void Set(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change();
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Summary));
    }

    public bool Enabled { get => Profile.Enabled; set => Set(() => Profile.Enabled = value); }
    public int LeftInner { get => Profile.LeftStick.InnerDeadzone; set => Set(() => Profile.LeftStick.InnerDeadzone = Math.Clamp(value, 0, 90)); }
    public int LeftOuter { get => Profile.LeftStick.OuterDeadzone; set => Set(() => Profile.LeftStick.OuterDeadzone = Math.Clamp(value, 0, 50)); }
    public int LeftAnti { get => Profile.LeftStick.AntiDeadzone; set => Set(() => Profile.LeftStick.AntiDeadzone = Math.Clamp(value, 0, 50)); }
    public int LeftSensitivity { get => Profile.LeftStick.Sensitivity; set => Set(() => Profile.LeftStick.Sensitivity = Math.Clamp(value, 50, 150)); }
    public StickCurve LeftCurve { get => Profile.LeftStick.Curve; set => Set(() => Profile.LeftStick.Curve = value); }
    public bool LeftInvert { get => Profile.LeftStick.InvertY; set => Set(() => Profile.LeftStick.InvertY = value); }
    public int RightInner { get => Profile.RightStick.InnerDeadzone; set => Set(() => Profile.RightStick.InnerDeadzone = Math.Clamp(value, 0, 90)); }
    public int RightOuter { get => Profile.RightStick.OuterDeadzone; set => Set(() => Profile.RightStick.OuterDeadzone = Math.Clamp(value, 0, 50)); }
    public int RightAnti { get => Profile.RightStick.AntiDeadzone; set => Set(() => Profile.RightStick.AntiDeadzone = Math.Clamp(value, 0, 50)); }
    public int RightSensitivity { get => Profile.RightStick.Sensitivity; set => Set(() => Profile.RightStick.Sensitivity = Math.Clamp(value, 50, 150)); }
    public StickCurve RightCurve { get => Profile.RightStick.Curve; set => Set(() => Profile.RightStick.Curve = value); }
    public bool RightInvert { get => Profile.RightStick.InvertY; set => Set(() => Profile.RightStick.InvertY = value); }
    public int TriggerDeadzone { get => Profile.TriggerDeadzone; set => Set(() => Profile.TriggerDeadzone = Math.Clamp(value, 0, 90)); }
    public int TriggerMax { get => Profile.TriggerMax; set => Set(() => Profile.TriggerMax = Math.Clamp(value, 10, 100)); }
    public int Vibration { get => Profile.Vibration; set => Set(() => Profile.Vibration = Math.Clamp(value, 0, 200)); }
    public bool SwapFace { get => Profile.SwapFaceButtons; set => Set(() => Profile.SwapFaceButtons = value); }

    public string Summary =>
        $"Left stick {Profile.LeftStick.InnerDeadzone}% deadzone, {Profile.LeftStick.Curve.ToString().ToLowerInvariant()} · Right stick {Profile.RightStick.InnerDeadzone}%, {Profile.RightStick.Curve.ToString().ToLowerInvariant()} · " +
        (Profile.Vibration == 0 ? "rumble off" : $"rumble {Profile.Vibration}%") + (Profile.SwapFaceButtons ? " · A/B swapped" : "");

    // ---- live preview (positions on a 120 px pad, centre 60)
    [ObservableProperty] private double _leftRawX = 54;
    [ObservableProperty] private double _leftRawY = 54;
    [ObservableProperty] private double _leftOutX = 52;
    [ObservableProperty] private double _leftOutY = 52;
    [ObservableProperty] private double _rightRawX = 54;
    [ObservableProperty] private double _rightRawY = 54;
    [ObservableProperty] private double _rightOutX = 52;
    [ObservableProperty] private double _rightOutY = 52;
    [ObservableProperty] private double _leftTriggerOut;
    [ObservableProperty] private double _rightTriggerOut;
    [ObservableProperty] private string _previewText = "Move the sticks to see the effect.";

    public void StartPreview()
    {
        if (_timer.IsEnabled) return;
        FitzLauncher.Windows.PlayStationPads.Shared.Start();
        _timer.Start();
    }

    public void StopPreview()
    {
        Flush();
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        FitzLauncher.Windows.PlayStationPads.Shared.Stop();
    }

    private void Poll()
    {
        if (ControllerDevices.ReadFirst() is not { } s) { PreviewText = "No controller connected."; return; }
        PreviewText = "Dot: where the stick is. Ring: what the game gets.";
        (LeftRawX, LeftRawY) = Pos(s.lx, s.ly, 6);
        (RightRawX, RightRawY) = Pos(s.rx, s.ry, 6);
        var (lx, ly) = ControllerMath.Stick(s.lx, s.ly, Profile.LeftStick);
        var (rx, ry) = ControllerMath.Stick(s.rx, s.ry, Profile.RightStick);
        (LeftOutX, LeftOutY) = Pos(lx, ly, 8);
        (RightOutX, RightOutY) = Pos(rx, ry, 8);
        LeftTriggerOut = ControllerMath.Trigger(s.lt, Profile.TriggerDeadzone, Profile.TriggerMax) / 255d * 100;
        RightTriggerOut = ControllerMath.Trigger(s.rt, Profile.TriggerDeadzone, Profile.TriggerMax) / 255d * 100;
    }

    private static (double, double) Pos(short x, short y, double half) => (60 + x / 32767d * 54 - half, 60 - y / 32767d * 54 - half);

    [RelayCommand] private Task TestRumbleAsync() => ControllerDevices.TestRumbleAsync(Profile.Vibration);

    [RelayCommand]
    private void Reset()
    {
        var d = new ControllerProfile { Enabled = Profile.Enabled };
        Profile.LeftStick = d.LeftStick;
        Profile.RightStick = d.RightStick;
        Profile.TriggerDeadzone = d.TriggerDeadzone;
        Profile.TriggerMax = d.TriggerMax;
        Profile.Vibration = d.Vibration;
        Profile.SwapFaceButtons = d.SwapFaceButtons;
        _save();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Settings → Controllers: what's connected, driver status, menu navigation and the default profile.</summary>
public sealed partial class ControllersViewModel : ObservableObject
{
    private readonly Services.AppServices _s;
    public ControllerProfileViewModel Default { get; }
    public ObservableCollection<string> Connected { get; } = [];
    [ObservableProperty] private string _driverText = "";
    [ObservableProperty] private bool _driversReady;

    public ControllersViewModel(Services.AppServices s)
    {
        _s = s;
        Default = new ControllerProfileViewModel(s.Settings.DefaultController, s.SaveSettings);
    }

    public bool GamepadNavigation { get => _s.Settings.GamepadNavigation; set { _s.Settings.GamepadNavigation = value; _s.SaveSettings(); OnPropertyChanged(); } }

    // ---- PS button → OneXConsole
    public bool PsButtonOpensConsole
    {
        get => _s.Settings.PsButtonOpensConsole;
        set
        {
            _s.Settings.PsButtonOpensConsole = value;
            _s.SaveSettings();
            _s.ApplyConsoleButton();
            OnPropertyChanged();
            UpdateConsoleText();
        }
    }

    // ---- PS + button shortcuts
    public bool CombosEnabled
    {
        get => _s.Settings.PadCombos.Enabled;
        set { _s.Settings.PadCombos.Enabled = value; _s.SaveSettings(); _s.ApplyConsoleButton(); OnPropertyChanged(); }
    }

    private List<PadComboRow>? _comboRows;
    public List<PadComboRow> ComboRows => _comboRows ??= [.. FitzLauncher.Core.Input.PadComboSettings.Buttons.Select(b => new PadComboRow(b.Button, b.Name, _s))];

    [RelayCommand]
    private void ResetCombos()
    {
        _s.Settings.PadCombos = FitzLauncher.Core.Input.PadComboSettings.New();
        _s.SaveSettings();
        _comboRows = null;
        OnPropertyChanged(nameof(ComboRows));
    }

    public bool ConsolePadMirror { get => _s.Settings.ConsolePadMirror; set { _s.Settings.ConsolePadMirror = value; _s.SaveSettings(); OnPropertyChanged(); } }

    [ObservableProperty] private string _consoleText = "";

    private void UpdateConsoleText()
    {
        var path = FitzLauncher.Windows.ConsoleBridge.Locate(_s.Settings.ConsoleAppPath);
        ConsoleText = path is null
            ? "OneXConsole wasn't found on this PC. Use Choose… to point to OneXConsole.exe (or its Start menu shortcut)."
            : $"Found: {path}";
    }

    [RelayCommand]
    private void ChooseConsole()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Find OneXConsole", Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk" };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.ConsoleAppPath = dlg.FileName;
        _s.SaveSettings();
        UpdateConsoleText();
    }

    [RelayCommand]
    private void TestConsole()
    {
        try { _s.ConsoleButton.Toggle(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ConsoleText = "Couldn't open OneXConsole: " + e.Message; }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var (pads, names, vigem, hid) = await Task.Run(() => (ControllerDevices.XInputPads(), ControllerDevices.DeviceNames(), ControllerDevices.ViGEmInstalled(), ControllerDevices.HidHideInstalled()));
        Connected.Clear();
        foreach (var p in pads) Connected.Add($"Player {p.Slot}: {p.Kind}" + (p.Battery.Length > 0 ? $" · {p.Battery}" : ""));
        foreach (var n in names.Where(n => !n.Contains("XINPUT", StringComparison.OrdinalIgnoreCase))) Connected.Add(n);
        // DualShock / DualSense read directly (menus, PS button, per-game settings).
        FitzLauncher.Windows.PlayStationPads.Shared.Start();
        await Task.Delay(400);
        foreach (var n in FitzLauncher.Windows.PlayStationPads.Shared.Connected) Connected.Add(n + ": works in menus and with per-game settings, no extra app needed");
        FitzLauncher.Windows.PlayStationPads.Shared.Stop();
        UpdateConsoleText();
        if (Connected.Count == 0) Connected.Add("No controller found. Plug one in or turn it on, then Refresh.");
        DriversReady = vigem && hid;
        DriverText = DriversReady
            ? "ViGEmBus and HidHide are installed: per-game controller settings will work."
            : $"Per-game controller settings need two free drivers by Nefarius: {(vigem ? "" : "ViGEmBus")}{(!vigem && !hid ? " and " : "")}{(hid ? "" : "HidHide")} {(vigem || hid ? "is" : "are")} missing. " +
              "They add a virtual controller and hide the real one from the game (the same drivers Handheld Companion and DS4Windows use). Menu navigation works without them.";
    }

    [RelayCommand] private static void GetViGEm() => ModsViewModel.Open("https://github.com/nefarius/ViGEmBus/releases");
    [RelayCommand] private static void GetHidHide() => ModsViewModel.Open("https://github.com/nefarius/HidHide/releases");
}

/// <summary>One "PS + button" row in Settings → Controllers.</summary>
public sealed partial class PadComboRow(ushort button, string name, Services.AppServices s) : ObservableObject
{
    public string Name { get; } = "PS + " + name;

    public static IReadOnlyList<KeyValuePair<FitzLauncher.Core.Input.PadAction, string>> Choices { get; } =
    [
        new(FitzLauncher.Core.Input.PadAction.None, "Nothing"),
        new(FitzLauncher.Core.Input.PadAction.VolumeUp, "Volume up"),
        new(FitzLauncher.Core.Input.PadAction.VolumeDown, "Volume down"),
        new(FitzLauncher.Core.Input.PadAction.Mute, "Mute / unmute"),
        new(FitzLauncher.Core.Input.PadAction.BrightnessUp, "Brightness up"),
        new(FitzLauncher.Core.Input.PadAction.BrightnessDown, "Brightness down"),
        new(FitzLauncher.Core.Input.PadAction.PlayPause, "Play / pause media"),
        new(FitzLauncher.Core.Input.PadAction.NextTrack, "Next track"),
        new(FitzLauncher.Core.Input.PadAction.PreviousTrack, "Previous track"),
        new(FitzLauncher.Core.Input.PadAction.Screenshot, "Screenshot (Win+PrtScn)"),
        new(FitzLauncher.Core.Input.PadAction.GameBar, "Xbox Game Bar"),
        new(FitzLauncher.Core.Input.PadAction.OnScreenKeyboard, "On-screen keyboard"),
        new(FitzLauncher.Core.Input.PadAction.SwitchWindow, "Switch window (Alt+Tab)"),
        new(FitzLauncher.Core.Input.PadAction.ShowDesktop, "Show desktop"),
        new(FitzLauncher.Core.Input.PadAction.TaskManager, "Task Manager"),
        new(FitzLauncher.Core.Input.PadAction.OpenLauncher, "Open Fitz Launcher"),
        new(FitzLauncher.Core.Input.PadAction.TrainerOrGame, "Switch trainer ↔ game"),
        new(FitzLauncher.Core.Input.PadAction.ToggleOverlay, "Hide / show FPS counters"),
        new(FitzLauncher.Core.Input.PadAction.Sleep, "Sleep"),
        new(FitzLauncher.Core.Input.PadAction.GameHelp, "Game help panel (AI Game Companion)"),
        new(FitzLauncher.Core.Input.PadAction.GameHelpAsk, "Game help: ask about the screen"),
    ];

    public FitzLauncher.Core.Input.PadAction Action
    {
        get => s.Settings.PadCombos.ActionFor(button);
        set
        {
            var list = s.Settings.PadCombos.Combos;
            list.RemoveAll(c => c.Button == button);
            if (value != FitzLauncher.Core.Input.PadAction.None) list.Add(new FitzLauncher.Core.Input.PadCombo { Button = button, Action = value });
            s.SaveSettings();
            OnPropertyChanged();
        }
    }
}
