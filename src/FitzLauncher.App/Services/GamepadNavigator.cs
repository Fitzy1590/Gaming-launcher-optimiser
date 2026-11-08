using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FitzLauncher.App.Services;

/// <summary>A window that handles some controller buttons itself (tabs, Play, back).</summary>
public interface IGamepadAware
{
    /// <returns>True when the window handled the button.</returns>
    bool OnGamepad(PadButton button);
}

/// <summary>
/// Drives every launcher window with a controller, the way a console menu works: D-pad or left stick moves
/// the focus (lists and tile grids follow it), A selects, B goes back or closes, LB/RB switch tabs, the
/// right stick scrolls. Only acts while a launcher window is in front, never during play, and Big Picture
/// keeps its own handling. Text boxes open the touch keyboard on A.
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly GamepadPoller _pad = new();
    private readonly Func<bool> _enabled;

    public GamepadNavigator(Func<bool> enabled)
    {
        _enabled = enabled;
        _pad.Pressed += OnPressed;
    }

    public void Start() => _pad.Start();
    public void Stop() => _pad.Stop();

    private void OnPressed(PadButton b)
    {
        if (!_enabled()) return;
        var w = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
        if (w is null)
        {
            DriveNativeDialog(b); // e.g. a Yes/No confirmation box
            return;
        }
        if (w is Views.BigPictureWindow) return;
        if (w is IGamepadAware aware && aware.OnGamepad(b)) return;

        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null or Window || !IsInside(focused, w))
        {
            // First press: put the focus somewhere visible instead of acting.
            w.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        switch (b)
        {
            case PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right: Move(w, focused, b); break;
            case PadButton.A: Activate(w, focused); break;
            case PadButton.B:
                if (!Send(w, Key.Escape) && w != Application.Current?.MainWindow) w.Close();
                break;
            case PadButton.ScrollUp: Scroll(w, focused, -1); break;
            case PadButton.ScrollDown: Scroll(w, focused, +1); break;
            case PadButton.LeftShoulder: Send(w, Key.Tab, ModifierKeys.Shift); break;
            case PadButton.RightShoulder: Send(w, Key.Tab); break;
        }
    }

    /// <summary>Raises a real key press through WPF's input system, so lists, tabs and handlers behave as with a keyboard.</summary>
    public static bool Send(Window w, Key key, ModifierKeys mods = ModifierKeys.None)
    {
        if (PresentationSource.FromVisual(w) is not { } source) return false;
        if (key == Key.Tab)
        {
            // Tab/Shift+Tab without touching the real keyboard state.
            var target = Keyboard.FocusedElement as UIElement;
            return target?.MoveFocus(new TraversalRequest(mods == ModifierKeys.Shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next)) ?? false;
        }
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        InputManager.Current.ProcessInput(down);
        var handled = down.Handled;
        if (!handled)
        {
            var kd = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
            InputManager.Current.ProcessInput(kd);
            handled = kd.Handled;
        }
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent });
        return handled;
    }

    /// <summary>
    /// D-pad / left stick. Sliders take left/right as value changes and lists, open drop-downs and text boxes keep
    /// their own arrow handling; everything else moves focus to the nearest control in that direction (bringing
    /// it into view). With nothing further that way, up/down scroll the page instead.
    /// </summary>
    private static void Move(Window w, DependencyObject focused, PadButton b)
    {
        var key = b switch { PadButton.Up => Key.Up, PadButton.Down => Key.Down, PadButton.Left => Key.Left, _ => Key.Right };
        var dir = b switch { PadButton.Up => FocusNavigationDirection.Up, PadButton.Down => FocusNavigationDirection.Down, PadButton.Left => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right };

        if (focused is Slider slider && b is PadButton.Left or PadButton.Right)
        {
            var step = slider.SmallChange > 0 ? slider.SmallChange : 1;
            slider.Value = Math.Clamp(slider.Value + (b == PadButton.Right ? step : -step), slider.Minimum, slider.Maximum);
            return;
        }
        if (focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true }) { Send(w, key); return; }
        if (focused is TextBoxBase && b is PadButton.Left or PadButton.Right) { Send(w, key); return; }

        var before = Keyboard.FocusedElement;
        if (focused is ListBoxItem or TreeViewItem)
        {
            // Let the list move within itself first (grids of game tiles wrap properly this way).
            Send(w, key);
            if (!ReferenceEquals(Keyboard.FocusedElement, before)) { BringIntoView(); return; }
        }
        if (focused is UIElement el && el.MoveFocus(new TraversalRequest(dir)) && !ReferenceEquals(Keyboard.FocusedElement, before))
        {
            BringIntoView();
            return;
        }
        if (b is PadButton.Up or PadButton.Down) Scroll(w, focused, b == PadButton.Up ? -1 : +1);
    }

    private static void BringIntoView()
    {
        if (Keyboard.FocusedElement is FrameworkElement fe)
            fe.BringIntoView(new Rect(new Point(0, -40), new Size(fe.ActualWidth, fe.ActualHeight + 80))); // a little margin above/below
    }

    private static void Activate(Window w, DependencyObject focused)
    {
        switch (focused)
        {
            case ComboBoxItem item when ItemsControl.ItemsControlFromItemContainer(item) is ComboBox combo:
                item.IsSelected = true;
                combo.IsDropDownOpen = false;
                combo.Focus();
                return;
            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return;
            case TextBoxBase:
                OpenTouchKeyboard();
                return;
            case ListBoxItem lbi:
                lbi.IsSelected = true;
                Send(w, Key.Enter); // e.g. Play in the library grid
                return;
        }
        if (focused is UIElement el && UIElementAutomationPeer.CreatePeerForElement(el) is { } peer)
        {
            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv && el.IsEnabled) { inv.Invoke(); return; }
            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog && el.IsEnabled) { tog.Toggle(); return; }
            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel && el.IsEnabled) { sel.Select(); return; }
            if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider ex)
            {
                if (ex.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Collapsed) ex.Expand(); else ex.Collapse();
                return;
            }
        }
        Send(w, Key.Enter);
    }

    private static void Scroll(Window w, DependencyObject focused, int dir)
    {
        var sv = Ancestor<ScrollViewer>(focused) ?? Descendant<ScrollViewer>(w);
        if (sv is null) return;
        for (var i = 0; i < 3; i++) { if (dir < 0) sv.LineUp(); else sv.LineDown(); }
    }

    /// <summary>The Windows touch keyboard, for typing with a controller on a handheld.</summary>
    private static void OpenTouchKeyboard()
    {
        var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
        try
        {
            if (File.Exists(tabTip)) Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static bool IsInside(DependencyObject d, Window w)
    {
        for (var x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == w) return true;
        return Window.GetWindow(d) == w;
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x is not null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        var q = new Queue<DependencyObject>([root]);
        while (q.Count > 0)
        {
            var d = q.Dequeue();
            if (d is T t && (d is not UIElement u || u.IsVisible)) return t;
            if (d is not Visual) continue;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    /// <summary>Standard Windows message boxes from the launcher: D-pad moves between buttons, A presses, B cancels.</summary>
    private static void DriveNativeDialog(PadButton b)
    {
        var fg = GetForegroundWindow();
        if (fg == 0 || GetWindowThreadProcessId(fg, out var pid) == 0 || pid != Environment.ProcessId) return;
        byte? vk = b switch
        {
            PadButton.Left or PadButton.Up => 0x25,   // VK_LEFT
            PadButton.Right or PadButton.Down => 0x27, // VK_RIGHT
            PadButton.A => 0x20,                       // VK_SPACE presses the focused button
            PadButton.B => 0x1B,                       // VK_ESCAPE
            _ => null,
        };
        if (vk is not { } k) return;
        keybd_event(k, 0, 0, 0);
        keybd_event(k, 0, 2, 0); // KEYEVENTF_KEYUP
    }

    public void Dispose() => _pad.Dispose();
}
