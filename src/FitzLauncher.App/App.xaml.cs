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

        // Undo a Game Mode / power limit left active by a crash or power cut, before anything else.
        var services = _services;
        _ = Task.Run(() =>
        {
            foreach (var line in services.GameMode.RecoverIfNeeded()) services.Log.Write("Recovered Game Mode: " + line);
            if (FitzLauncher.Windows.RyzenAdjClient.Find(services.Settings.RyzenAdjPath) is { } adj)
                new FitzLauncher.Windows.RyzenAdjClient(adj, services.Paths).RecoverIfNeeded();
        });

        // Newer library from another PC (or from before a reinstall)? Offer it before loading.
        if (_services.Sync is { } sync && sync.RemoteIsNewer(Environment.MachineName) && sync.Remote() is { } remote &&
            MessageBox.Show($"Your sync folder has a newer library from {remote.Machine} ({remote.Updated.ToLocalTime():d MMM HH:mm}).\n\nUse it on this PC? Your current data is backed up first.",
                "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            sync.Download();
            _services.Dispose();
            _services = new AppServices();
        }

        // The last undervolted session never finished (freeze, crash, power loss): back the undervolt off.
        string? undervoltNote = null;
        if (FitzLauncher.Windows.RyzenAdjClient.Find(_services.Settings.RyzenAdjPath) is { } uvAdj)
        {
            var client = new FitzLauncher.Windows.RyzenAdjClient(uvAdj, _services.Paths);
            if (client.UndervoltCrashedLastTime())
            {
                undervoltNote = FitzLauncher.Core.Optimization.UndervoltLadder.AfterCrash(_services.Settings);
                _services.SaveSettings();
                _ = Task.Run(client.ClearUndervolt);
                if (undervoltNote is not null) _services.Log.Write(undervoltNote);
            }
        }

        var vm = new MainViewModel(_services);
        if (undervoltNote is not null) vm.ShowToast("The last game didn't close normally. " + undervoltNote);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        // Started by the "start with Windows" task: stay out of the way.
        if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.WindowState = WindowState.Minimized;

        // First run: scan the hardware so tweaks can be tailored. Cached afterwards.
        _ = vm.Hardware.EnsureScannedAsync();
        // Tell the user about games added to their saved game folders since last time.
        _ = vm.CheckLibraryFoldersAsync();
        _ = vm.CheckForUpdatesOnStartAsync();
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
