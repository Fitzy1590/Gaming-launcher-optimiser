using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.ViewModels;
using Microsoft.Win32;

namespace FitzLauncher.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

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
        vm.SessionStarted += () => { if (vm.Settings.S.MinimizeWhilePlaying) WindowState = WindowState.Minimized; };
        vm.SessionEnded += () =>
        {
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
        {
            _vm.Services.Log.Write("Ctrl+Alt+B is taken by another app; benchmark hotkey unavailable.");
            return;
        }
        HwndSource.FromHwnd(helper.Handle)?.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam == HotkeyId)
            {
                _vm.StartBenchmark();
                handled = true;
            }
            return 0;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
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

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick a folder containing your games (each subfolder becomes a game)" };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        var subfolders = Directory.GetDirectories(folder);
        // If the picked folder is itself a game (has an exe in its root), add just it.
        var targets = Directory.EnumerateFiles(folder, "*.exe").Any() || subfolders.Length == 0 ? [folder] : subfolders;
        await _vm.AddPathsAsync(targets);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await _vm.AddPathsAsync(paths);
    }

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
