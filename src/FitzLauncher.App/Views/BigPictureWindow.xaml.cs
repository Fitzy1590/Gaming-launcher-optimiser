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
            Key.Q => PadButton.LeftShoulder, Key.E => PadButton.RightShoulder, _ => null,
        };
        if (b is null) return;
        OnPad(b.Value);
        e.Handled = true;
    }

    private void OnPad(PadButton b)
    {
        if (_vm.IsPlaying) return;
        var details = _vm.Details;
        switch (b)
        {
            case PadButton.Left: Move(-1); break;
            case PadButton.Right: Move(1); break;
            case PadButton.A or PadButton.Start:
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
