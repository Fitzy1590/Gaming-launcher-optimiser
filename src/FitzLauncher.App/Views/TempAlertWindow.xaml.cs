using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FitzLauncher.Windows;

namespace FitzLauncher.App.Views;

/// <summary>
/// High-temperature toast drawn on top of the game. It never takes focus and is click-through, so it
/// can't minimise or interrupt a game. It shows over borderless/windowed games; exclusive
/// fullscreen hides other windows, which is why there's also a sound.
/// </summary>
public partial class TempAlertWindow : Window
{
    private readonly bool _sound;
    private readonly bool _overlay;

    public TempAlertWindow(TempAlert alert, bool sound, bool overlay)
    {
        InitializeComponent();
        _sound = sound;
        _overlay = overlay;
        Title_.Text = $"{alert.Component} at {alert.TempC:0} °C";
        Body.Text = alert.Component == "GPU"
            ? $"Above your {alert.ThresholdC:0} °C limit. Try a lower FPS target, or check the fans and airflow."
            : $"Above your {alert.ThresholdC:0} °C limit. Check the CPU cooler and airflow, and close heavy background apps.";
    }

    public void ShowNonActivating()
    {
        if (_sound) SystemSounds.Exclamation.Play();
        if (!_overlay) return;
        var area = SystemParameters.WorkArea;
        Left = area.Right - 400;
        Top = area.Top + 20;
        Show();
        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        close.Tick += (_, _) => { close.Stop(); Close(); };
        close.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint hwnd, int index, nint value);
}
