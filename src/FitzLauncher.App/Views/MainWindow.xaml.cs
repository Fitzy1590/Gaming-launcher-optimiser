using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Page = FitzLauncher.App.ViewModels.Page;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.ViewModels;
using Microsoft.Win32;

namespace FitzLauncher.App.Views;

public partial class MainWindow : Window, FitzLauncher.App.Services.IGamepadAware
{
    private readonly MainViewModel _vm;
    private readonly FitzLauncher.App.Services.GamepadNavigator _nav;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        FocusSearchCommand = new RelayCommand(() => { _vm.Page = Page.Library; SearchBox.Focus(); SearchBox.SelectAll(); });
        AddGameCommand = new RelayCommand(() => AddGame_Click(this, new RoutedEventArgs()));
        InputBindings.Add(new KeyBinding(FocusSearchCommand, Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(AddGameCommand, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.BigPictureCommand, Key.F11, ModifierKeys.None));
        vm.OpenBigPicture = () =>
        {
            var bp = new BigPictureWindow(vm) { Owner = this };
            bp.Show();
            bp.Activate();
        };

        vm.Confirm = text => MessageBox.Show(this, text, "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        vm.EditGameDialog = game => new EditGameWindow(game) { Owner = this }.ShowDialog() == true;
        vm.TempAlertRaised += a => new TempAlertWindow(a, vm.Settings.S.TempAlertSound, vm.Settings.S.TempAlertOverlay).ShowNonActivating();
        // Controller navigation for every window (never while a game runs).
        _nav = new FitzLauncher.App.Services.GamepadNavigator(() => vm.Settings.S.GamepadNavigation && !vm.IsPlaying);
        if (!vm.SafeMode) _nav.Start();
        Closed += (_, _) => _nav.Dispose();
        vm.SessionStarted += () => { _nav.Stop(); if (vm.Settings.S.MinimizeWhilePlaying) WindowState = WindowState.Minimized; };
        vm.SessionEnded += () =>
        {
            if (!vm.SafeMode) _nav.Start();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };

        Drop += OnDrop;
        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            RegisterBenchmarkHotkey();
        };
    }

    // ------------------------------------------------------------------ global benchmark hotkey

    private const int HotkeyId = 0xF17;
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>Ctrl+Alt+B works while a game has focus (RegisterHotKey is system-wide).</summary>
    private void RegisterBenchmarkHotkey()
    {
        var helper = new WindowInteropHelper(this);
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        if (!RegisterHotKey(helper.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x42 /* B */))
            _vm.Services.Log.Write("Ctrl+Alt+B is taken by another app; benchmark hotkey unavailable.");
        // In-game FPS target switch (keyboard alternative to View + D-pad).
        RegisterHotKey(helper.Handle, HotkeyId + 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x26 /* Up */);
        RegisterHotKey(helper.Handle, HotkeyId + 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x28 /* Down */);
        HwndSource.FromHwnd(helper.Handle)?.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg != WM_HOTKEY) return 0;
            if (wParam == HotkeyId) { _vm.StartBenchmark(); handled = true; }
            else if (wParam == HotkeyId + 1 && _vm.Services.Settings.QuickSwitch) { _vm.QuickStep(+1); handled = true; }
            else if (wParam == HotkeyId + 2 && _vm.Services.Settings.QuickSwitch) { _vm.QuickStep(-1); handled = true; }
            return 0;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(h, HotkeyId);
        UnregisterHotKey(h, HotkeyId + 1);
        UnregisterHotKey(h, HotkeyId + 2);
        base.OnClosed(e);
    }

    public IRelayCommand FocusSearchCommand { get; }
    public IRelayCommand AddGameCommand { get; }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add games: pick the game's .exe or a shortcut",
            Filter = "Games (*.exe;*.lnk)|*.exe;*.lnk|All files|*.*",
            Multiselect = true,
            DereferenceLinks = false,
        };
        if (dlg.ShowDialog(this) == true) await _vm.AddPathsAsync(dlg.FileNames);
    }

    private void ScanFolders_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ScanFoldersWindow(new ScanFoldersViewModel(_vm.Services, _vm)) { Owner = this };
        dlg.ShowDialog();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await _vm.AddPathsAsync(paths);
    }

    private static readonly Page[] PageOrder = [Page.Home, Page.Library, Page.Mods, Page.Storage, Page.Hardware, Page.Sessions, Page.Settings, Page.About];

    /// <summary>Console-style buttons: LB/RB pages, Start plays, X mods, Y search, View Big Picture, B back.</summary>
    public bool OnGamepad(FitzLauncher.App.Services.PadButton b)
    {
        switch (b)
        {
            case FitzLauncher.App.Services.PadButton.LeftShoulder or FitzLauncher.App.Services.PadButton.RightShoulder:
                var i = Array.IndexOf(PageOrder, _vm.Page);
                _vm.Page = PageOrder[(i + (b == FitzLauncher.App.Services.PadButton.RightShoulder ? 1 : PageOrder.Length - 1)) % PageOrder.Length];
                FocusPage();
                return true;
            case FitzLauncher.App.Services.PadButton.Start when _vm.Selected is { } g:
                _ = _vm.PlayAsync(g);
                return true;
            case FitzLauncher.App.Services.PadButton.X when _vm.Selected is { } g:
                _vm.OpenMods(g.Game);
                return true;
            case FitzLauncher.App.Services.PadButton.Y:
                FocusSearchCommand.Execute(null);
                return true;
            case FitzLauncher.App.Services.PadButton.Back:
                _vm.BigPictureCommand.Execute(null);
                return true;
            case FitzLauncher.App.Services.PadButton.B:
                if (FitzLauncher.App.Services.GamepadNavigator.Send(this, Key.Escape)) return true;
                if (_vm.Page != Page.Library) { _vm.Page = Page.Library; FocusPage(); }
                else GameList.Focus();
                return true;
            default:
                return false;
        }
    }

    /// <summary>After a page change, put the focus on the page so the D-pad works straight away.</summary>
    private void FocusPage() => Dispatcher.BeginInvoke(() =>
    {
        if (_vm.Page == Page.Library)
        {
            var item = GameList.SelectedItem ?? (GameList.Items.Count > 0 ? GameList.Items[0] : null);
            if (item is not null && GameList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi) { lbi.Focus(); return; }
            GameList.Focus();
            return;
        }
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void GameList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.Selected is { } g) { _ = _vm.PlayAsync(g); e.Handled = true; }
        else if (e.Key == Key.Escape) _vm.Selected = null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsPlaying && MessageBox.Show(this,
                "A game is running. Closing the launcher now ends Game Mode and stops FPS recording for this session. Close anyway?",
                "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _vm.StopSession();
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = 0x00201A16; // COLORREF (BGR) of Surface #161A20
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
    }
}
