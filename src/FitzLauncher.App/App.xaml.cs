using System.Windows;
using System.Windows.Threading;
using FitzLauncher.App.Services;
using FitzLauncher.App.ViewModels;
using FitzLauncher.App.Views;

namespace FitzLauncher.App;

public partial class App : Application
{
    private static Mutex? _single;
    private static bool _ownsMutex;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\FitzLauncher.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _services = new AppServices();
        DispatcherUnhandledException += OnUnhandled;

        // Undo a Game Mode left active by a crash or power cut, before anything else.
        var services = _services;
        _ = Task.Run(() =>
        {
            foreach (var line in services.GameMode.RecoverIfNeeded()) services.Log.Write("Recovered Game Mode: " + line);
        });

        var vm = new MainViewModel(_services);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        // First run: scan the hardware so tweaks can be tailored. Cached afterwards.
        _ = vm.Hardware.EnsureScannedAsync();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write("Unhandled: " + e.Exception);
        if (MainWindow?.DataContext is MainViewModel vm) vm.ShowToast("Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            if (_services.GameMode.IsActive) _services.GameMode.Restore();
            _services.SaveSettings();
            _services.SaveLibrary();
            _services.Dispose();
        }
        if (_ownsMutex) _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
