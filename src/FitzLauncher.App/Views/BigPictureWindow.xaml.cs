using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FitzLauncher.App.Services;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

/// <summary>Fullscreen, controller-first library: big covers, Play, and the 1-click FPS targets.</summary>
public partial class BigPictureWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly GamepadPoller _pad = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    public BigPictureWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _pad.Pressed += OnPad;
        _clock.Tick += (_, _) => Clock.Text = DateTime.Now.ToString("HH:mm");
        Clock.Text = DateTime.Now.ToString("HH:mm");

        vm.SessionStarted += OnSessionStarted;
        vm.SessionEnded += OnSessionEnded;
        Loaded += (_, _) =>
        {
            if (_vm.Selected is null && _vm.GamesView.Count > 0) _vm.Selected = (GameViewModel)_vm.GamesView.GetItemAt(0);
            Carousel.Focus();
            ScrollToSelected();
            _pad.Start();
            _clock.Start();
        };
        Activated += (_, _) => { if (!_vm.IsPlaying) _pad.Start(); };
        Deactivated += (_, _) => _pad.Stop(); // no stray input while you're in a game or another app
        KeyDown += OnKey;
    }

    private void OnSessionStarted() => WindowState = WindowState.Minimized;

    private void OnSessionEnded()
    {
        WindowState = WindowState.Maximized;
        Activate();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        PadButton? b = e.Key switch
        {
            Key.Left => PadButton.Left, Key.Right => PadButton.Right, Key.Enter => PadButton.A,
            Key.Escape or Key.Back => PadButton.B, Key.X => PadButton.X, Key.F => PadButton.Y,
            Key.Q => PadButton.LeftShoulder, Key.E => PadButton.RightShoulder, Key.Tab => PadButton.Start,
            Key.Up => PadButton.Up, Key.Down => PadButton.Down, _ => null,
        };
        if (b is null) return;
        OnPad(b.Value);
        e.Handled = true;
    }

    private void OnPad(PadButton b)
    {
        if (_vm.IsPlaying) return;
        if (OptionsPanel.Visibility == Visibility.Visible) { OnOptionsPad(b); return; }
        var details = _vm.Details;
        switch (b)
        {
            case PadButton.Start when details is not null:
                OpenOptions(details);
                return;
            case PadButton.Left: Move(-1); break;
            case PadButton.Right: Move(1); break;
            case PadButton.A:
                if (_vm.Selected is { } g) _ = _vm.PlayAsync(g);
                break;
            case PadButton.X:
                if (details is { IsBusy: false }) details.OptimiseCommand.Execute(null);
                break;
            case PadButton.LeftShoulder or PadButton.RightShoulder when details is { IsBusy: false }:
            {
                var targets = details.Targets.ToList();
                var i = targets.FindIndex(t => t.IsSelected);
                var next = Math.Clamp((i < 0 ? 0 : i) + (b == PadButton.RightShoulder ? 1 : -1), 0, targets.Count - 1);
                if (next != i) details.OptimiseAtCommand.Execute(targets[next].Value);
                break;
            }
            case PadButton.Y when details is not null:
                details.Favourite = !details.Favourite;
                break;
            case PadButton.B or PadButton.Back:
                Close();
                break;
        }
    }

    // ------------------------------------------------------------------ per-game options panel

    private List<BigPictureOption> _options = [];
    private int _optionIndex;

    private void OpenOptions(GameDetailsViewModel d)
    {
        var g = d.Game;
        _options =
        [
            new("Cooling", () => g.Cooling?.ToString() ?? $"Default ({_vm.Services.Settings.DefaultCooling})", () => d.CoolingText,
                dir => d.PickCooling(Cycle<FitzLauncher.Core.Optimization.CoolingMode?>([null, .. Enum.GetValues<FitzLauncher.Core.Optimization.CoolingMode>().Cast<FitzLauncher.Core.Optimization.CoolingMode?>()], g.Cooling, dir))),
            new("FPS cap on this screen", () => d.DeviceCapChoices.FirstOrDefault(c => c.IsSelected)?.Label ?? "Game target", () => d.CurrentScreenText,
                dir => d.PickDeviceCap(Cycle(d.DeviceCapChoices.Select(c => c.Value).ToList(), d.DeviceCapChoices.FirstOrDefault(c => c.IsSelected)?.Value, dir))),
            new("Controller settings", () => d.UseCustomController ? "Custom" : "Off", () => d.ControllerText,
                _ => d.UseCustomController = !d.UseCustomController),
            new("Trainer with the game", () => !d.HasTrainer ? "Not set up" : d.TrainerLaunchWithGame ? "On" : "Off", () => d.HasTrainer ? d.TrainerName : "Set one up on the game page.",
                _ => { if (d.HasTrainer) d.TrainerLaunchWithGame = !d.TrainerLaunchWithGame; }),
            new("Mods", () => g.ModSetup.Mods.Count == 0 ? "None" : $"{g.ModSetup.Mods.Count(m => m.Enabled)} on", () => "A: open the Mods window (works with the controller).",
                null, () => _vm.OpenMods(g)),
            new("Game update", () => d.UpdateStatus, () => d.LatestText.Length > 0 ? d.LatestText : d.UpdateDetail,
                null, () => d.CheckGameUpdateCommand.Execute(null)),
            new("Last session stutters", () => d.HasStutterReport ? $"{d.StutterCauses.Count} cause(s)" : "No report", () => d.HasStutterReport ? d.StutterSummary : "Play a session to get a report.",
                null, null),
        ];
        _optionIndex = 0;
        OptionsTitle.Text = g.Name;
        RefreshOptions();
        OptionsPanel.Visibility = Visibility.Visible;
    }

    private static T Cycle<T>(IReadOnlyList<T> values, T current, int dir)
    {
        var i = values.ToList().IndexOf(current);
        return values[((i < 0 ? 0 : i) + dir + values.Count) % values.Count];
    }

    private void RefreshOptions()
    {
        for (var i = 0; i < _options.Count; i++) _options[i].Refresh(i == _optionIndex);
        OptionsList.ItemsSource = null;
        OptionsList.ItemsSource = _options;
    }

    private void OnOptionsPad(PadButton b)
    {
        var o = _options[_optionIndex];
        switch (b)
        {
            case PadButton.Up: _optionIndex = Math.Max(0, _optionIndex - 1); break;
            case PadButton.Down: _optionIndex = Math.Min(_options.Count - 1, _optionIndex + 1); break;
            case PadButton.Left: o.Change?.Invoke(-1); break;
            case PadButton.Right: o.Change?.Invoke(+1); break;
            case PadButton.A: if (o.Activate is not null) o.Activate(); else o.Change?.Invoke(+1); break;
            case PadButton.B or PadButton.Start or PadButton.Back:
                OptionsPanel.Visibility = Visibility.Collapsed;
                Carousel.Focus();
                return;
        }
        RefreshOptions();
    }

    private void Move(int delta)
    {
        var count = _vm.GamesView.Count;
        if (count == 0) return;
        var index = _vm.Selected is null ? -1 : _vm.GamesView.IndexOf(_vm.Selected);
        var next = Math.Clamp(index + delta, 0, count - 1);
        _vm.Selected = (GameViewModel)_vm.GamesView.GetItemAt(next);
        ScrollToSelected();
    }

    private void ScrollToSelected()
    {
        if (_vm.Selected is not null) Carousel.ScrollIntoView(_vm.Selected);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pad.Dispose();
        _clock.Stop();
        _vm.SessionStarted -= OnSessionStarted;
        _vm.SessionEnded -= OnSessionEnded;
        Owner?.Activate();
        base.OnClosed(e);
    }
}

/// <summary>One row of Big Picture's options panel.</summary>
public sealed class BigPictureOption(string label, Func<string> value, Func<string> hint, Action<int>? change, Action? activate = null)
{
    public string Label { get; } = label;
    public string Value { get; private set; } = "";
    public string Hint { get; private set; } = "";
    public bool IsSelected { get; private set; }
    public Action<int>? Change { get; } = change;
    public Action? Activate { get; } = activate;

    public void Refresh(bool selected)
    {
        Value = value();
        Hint = hint();
        IsSelected = selected;
    }
}
