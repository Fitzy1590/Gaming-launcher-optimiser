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
    public ControllerProfile Profile { get; }

    public ControllerProfileViewModel(ControllerProfile profile, Action save)
    {
        Profile = profile;
        _save = save;
        _timer.Tick += (_, _) => Poll();
    }

    public static IReadOnlyList<StickCurve> Curves { get; } = Enum.GetValues<StickCurve>();

    private void Set(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change();
        _save();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Summary));
    }

    public bool Enabled { get => Profile.Enabled; set => Set(() => Profile.Enabled = value); }
    public int LeftInner { get => Profile.LeftStick.InnerDeadzone; set => Set(() => Profile.LeftStick.InnerDeadzone = value); }
    public int LeftOuter { get => Profile.LeftStick.OuterDeadzone; set => Set(() => Profile.LeftStick.OuterDeadzone = value); }
    public int LeftAnti { get => Profile.LeftStick.AntiDeadzone; set => Set(() => Profile.LeftStick.AntiDeadzone = value); }
    public int LeftSensitivity { get => Profile.LeftStick.Sensitivity; set => Set(() => Profile.LeftStick.Sensitivity = value); }
    public StickCurve LeftCurve { get => Profile.LeftStick.Curve; set => Set(() => Profile.LeftStick.Curve = value); }
    public bool LeftInvert { get => Profile.LeftStick.InvertY; set => Set(() => Profile.LeftStick.InvertY = value); }
    public int RightInner { get => Profile.RightStick.InnerDeadzone; set => Set(() => Profile.RightStick.InnerDeadzone = value); }
    public int RightOuter { get => Profile.RightStick.OuterDeadzone; set => Set(() => Profile.RightStick.OuterDeadzone = value); }
    public int RightAnti { get => Profile.RightStick.AntiDeadzone; set => Set(() => Profile.RightStick.AntiDeadzone = value); }
    public int RightSensitivity { get => Profile.RightStick.Sensitivity; set => Set(() => Profile.RightStick.Sensitivity = value); }
    public StickCurve RightCurve { get => Profile.RightStick.Curve; set => Set(() => Profile.RightStick.Curve = value); }
    public bool RightInvert { get => Profile.RightStick.InvertY; set => Set(() => Profile.RightStick.InvertY = value); }
    public int TriggerDeadzone { get => Profile.TriggerDeadzone; set => Set(() => Profile.TriggerDeadzone = value); }
    public int TriggerMax { get => Profile.TriggerMax; set => Set(() => Profile.TriggerMax = value); }
    public int Vibration { get => Profile.Vibration; set => Set(() => Profile.Vibration = value); }
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

    public void StartPreview() => _timer.Start();
    public void StopPreview() => _timer.Stop();

    private void Poll()
    {
        if (ControllerDevices.ReadFirst() is not { } s) { PreviewText = "No Xbox-style controller connected."; return; }
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

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var (pads, names, vigem, hid) = await Task.Run(() => (ControllerDevices.XInputPads(), ControllerDevices.DeviceNames(), ControllerDevices.ViGEmInstalled(), ControllerDevices.HidHideInstalled()));
        Connected.Clear();
        foreach (var p in pads) Connected.Add($"Player {p.Slot}: {p.Kind}" + (p.Battery.Length > 0 ? $" · {p.Battery}" : ""));
        foreach (var n in names.Where(n => !n.Contains("XINPUT", StringComparison.OrdinalIgnoreCase))) Connected.Add(n);
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
