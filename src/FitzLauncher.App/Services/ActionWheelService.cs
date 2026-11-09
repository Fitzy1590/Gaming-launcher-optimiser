using System.Windows;
using System.Windows.Threading;
using FitzLauncher.App.Views;
using FitzLauncher.Core.Input;
using FitzLauncher.Windows;

namespace FitzLauncher.App.Services;

/// <summary>Shows and hides the PS actions wheel as the controller bridge reports it, on the UI thread.</summary>
public sealed class ActionWheelService(AppServices s)
{
    private ActionWheelWindow? _window;
    private DispatcherTimer? _previewTimer;
    private int _previewSlot;

    public void Attach(ConsoleBridge bridge) => bridge.WheelChanged += state =>
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(state));

    private void Apply(WheelState state)
    {
        _previewTimer?.Stop();
        if (!state.Open) { _window?.Hide(); return; }
        _window ??= new ActionWheelWindow();
        _window.Present(s.Settings.PadCombos, s.Settings.PsButtonOpensConsole, state.Slot);
    }

    /// <summary>Settings → Preview: shows the wheel for a few seconds, stepping through the slots.</summary>
    public void Preview()
    {
        _window ??= new ActionWheelWindow();
        _previewSlot = 0;
        _window.Present(s.Settings.PadCombos, s.Settings.PsButtonOpensConsole, _previewSlot);
        _previewTimer?.Stop();
        var ticks = 0;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _previewTimer.Tick += (_, _) =>
        {
            if (++ticks > 9) { _previewTimer!.Stop(); _window?.Hide(); return; }
            _window?.Select(_previewSlot = (_previewSlot + 1) % PadComboSettings.WheelSlots);
        };
        _previewTimer.Start();
    }
}
