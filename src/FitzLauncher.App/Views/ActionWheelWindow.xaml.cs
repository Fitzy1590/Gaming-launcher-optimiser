using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FitzLauncher.App.ViewModels;
using FitzLauncher.Core.Input;

namespace FitzLauncher.App.Views;

/// <summary>
/// The PS actions wheel over a game: eight slots (flick the right stick to one, release PS to run it) and a legend of
/// every PS + button shortcut. Click-through, topmost and never activated, so the game keeps the focus. Shows over
/// borderless games (not exclusive fullscreen).
/// </summary>
public partial class ActionWheelWindow : Window
{
    private const double CentreX = 260, CentreY = 200, RadiusX = 190, RadiusY = 150, SlotW = 164, SlotH = 52;
    private readonly Border[] _slots = new Border[PadComboSettings.WheelSlots];
    private readonly TextBlock[] _labels = new TextBlock[PadComboSettings.WheelSlots];
    private readonly TextBlock _centreTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 200 };
    private readonly TextBlock _centreHint = new() { FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 200 };
    private PadComboSettings _cfg = new();

    public ActionWheelWindow()
    {
        InitializeComponent();
        _centreTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _centreHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        for (var i = 0; i < _slots.Length; i++)
        {
            var angle = i * Math.PI / 4;
            _labels[i] = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            _labels[i].SetResourceReference(TextBlock.ForegroundProperty, "Text");
            _slots[i] = new Border { Width = SlotW, Height = SlotH, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), Child = _labels[i] };
            Canvas.SetLeft(_slots[i], CentreX + RadiusX * Math.Sin(angle) - SlotW / 2);
            Canvas.SetTop(_slots[i], CentreY - RadiusY * Math.Cos(angle) - SlotH / 2);
            Ring.Children.Add(_slots[i]);
        }
        var centre = new StackPanel { Width = 200 };
        centre.Children.Add(_centreTitle);
        centre.Children.Add(_centreHint);
        Canvas.SetLeft(centre, CentreX - 100);
        Canvas.SetTop(centre, CentreY - 36);
        Ring.Children.Add(centre);
        Select(-1);
    }

    /// <summary>Fills in the slots and legend from the settings and shows the wheel over the game's screen.</summary>
    public void Present(PadComboSettings cfg, bool tapOpensConsole, int slot)
    {
        _cfg = cfg;
        for (var i = 0; i < _labels.Length; i++)
        {
            var a = cfg.WheelAction(i);
            _labels[i].Text = a == PadAction.None ? "—" : PadActionLabels.Label(a);
        }
        Legend.ItemsSource = PadComboSettings.Buttons
            .Select(b => (b.Name, Action: cfg.ActionFor(b.Button)))
            .Where(x => x.Action != PadAction.None)
            .Select(x => new LegendRow(x.Name, PadActionLabels.Label(x.Action)))
            .ToList();
        TapHint.Text = tapOpensConsole ? "A quick tap of PS opens OneXConsole." : "";
        Select(slot);
        if (!IsVisible) Show();
        Place();
    }

    /// <summary>Highlights a slot (-1 = none) and says what releasing PS will do.</summary>
    public void Select(int slot)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            var on = i == slot;
            _slots[i].SetResourceReference(Border.BackgroundProperty, on ? "Surface2" : "Surface");
            _slots[i].SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "BorderBrush");
            _labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (slot >= 0 && _cfg.WheelAction(slot) is var a and not PadAction.None)
        {
            _centreTitle.Text = PadActionLabels.Label(a);
            _centreHint.Text = "Release PS to run it";
        }
        else
        {
            _centreTitle.Text = "Actions";
            _centreHint.Text = "Flick the right stick to a slot, then release PS. Release now to cancel.";
        }
    }

    // ---- placement: centred on the screen the game is on, without taking focus

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        int x, y;
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            x = info.Monitor.Left + (info.Monitor.Right - info.Monitor.Left - (int)(DesiredSize.Width * scale)) / 2;
            y = info.Monitor.Top + (info.Monitor.Bottom - info.Monitor.Top - (int)(DesiredSize.Height * scale)) / 2;
        }
        else
        {
            x = (int)((SystemParameters.PrimaryScreenWidth - DesiredSize.Width) / 2 * scale);
            y = (int)((SystemParameters.PrimaryScreenHeight - DesiredSize.Height) / 2 * scale);
        }
        // HWND_TOPMOST, no size change, no activation
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010 | 0x0040);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLong(nint hwnd, int index, nint value);
}

/// <summary>One line of the wheel's legend: "Cross" → "Mute".</summary>
public sealed record LegendRow(string Button, string Action);
