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
    private readonly CancellationTokenSource _nxmStop = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        var nxm = FitzLauncher.App.Services.Mods.NxmHandler.FromArgs(e.Args);
        _single = new Mutex(true, @"Local\FitzLauncher.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // Started by a Nexus "Mod Manager Download" link: hand it to the launcher that's already open.
            if (nxm is not null) FitzLauncher.App.Services.Mods.NxmHandler.SendToRunning(nxm);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Crash reporting is set up before anything else can fail.
        var crashPaths = FitzLauncher.Core.Storage.AppPaths.Default();
        try { crashPaths.EnsureCreated(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var previousCrash = CrashReporter.Start(crashPaths);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // Last chance: don't leave a sleeping game frozen if the launcher itself dies.
            try { FitzLauncher.Windows.GameFreezer.RecoverIfNeeded(crashPaths); } catch (Exception) { }
            if (args.ExceptionObject is Exception ex) CrashReporter.Fatal(ex, "unhandled error (background)");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.Log.Write("Background task error: " + args.Exception);
            args.SetObserved(); // logged; never let a stray background task close the launcher
        };

        try { _services = new AppServices(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashReporter.Show(FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(ex),
                FitzLauncher.Core.Diagnostics.ErrorExplainer.Report(ex, CrashReporter.Version, CrashReporter.Os, "start-up (loading library and settings)"), fatal: true);
            Shutdown();
            return;
        }

        // Undo a Game Mode / power limit left active by a crash or power cut, before anything else.
        var services = _services;
        _ = Task.Run(() =>
        {
            foreach (var line in services.GameMode.RecoverIfNeeded()) services.Log.Write("Recovered Game Mode: " + line);
            if (FitzLauncher.Windows.GameFreezer.RecoverIfNeeded(services.Paths)) services.Log.Write("Unpaused a game left paused for sleep");
            FitzLauncher.Windows.PowerRequestOverride.Clear(services.Paths);
            FitzLauncher.Windows.ControllerRemapper.RecoverIfNeeded(services.Paths);
            FitzLauncher.Windows.CoolingController.RecoverIfNeeded(services.Paths);
            if (File.Exists(FitzLauncher.Windows.AmdFeatures.RestoreFile(services.Paths))) FitzLauncher.Windows.AmdFeatures.RestoreAfterSession(services.Paths);
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

        var vm = new MainViewModel(_services) { SafeMode = CrashReporter.SafeMode };
        if (undervoltNote is not null) vm.ShowToast("The last game didn't close normally. " + undervoltNote);
        if (previousCrash is { } pc) Dispatcher.BeginInvoke(() => CrashReporter.Show(pc.error, pc.report, fatal: false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Nexus "Mod Manager Download" links: from this start-up, and from later clicks while running.
        if (!CrashReporter.SafeMode) _ = FitzLauncher.App.Services.Mods.NxmHandler.ListenAsync(link => Dispatcher.BeginInvoke(() => _ = vm.HandleNxmAsync(link)), _nxmStop.Token);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        // Started by the "start with Windows" task: stay out of the way.
        if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.WindowState = WindowState.Minimized;

        // First run: scan the hardware so tweaks can be tailored. Cached afterwards.
        // Start-up counts as successful once the window has been up for 20 seconds.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => CrashReporter.MarkStarted(), TaskScheduler.Default);
        // Daily snapshot of the library and settings (kept 14 days).
        _ = Task.Run(() =>
        {
            try { FitzLauncher.Core.Storage.LibrarySnapshots.TakeIfDue(_services.Paths); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _services.Log.Write("Snapshot failed: " + ex.Message); }
        });
        if (CrashReporter.SafeMode)
        {
            FitzLauncher.App.Services.GameTheme.Enabled = false;
            return; // skip the hardware scan, update checks and other start-up work
        }
        _ = vm.Hardware.EnsureScannedAsync();
        // Tell the user about games added to their saved game folders since last time.
        _ = vm.CheckLibraryFoldersAsync();
        _ = vm.CheckForUpdatesOnStartAsync();
        _ = vm.CheckGameUpdatesDailyAsync();
        _ = vm.DeviceCheckOnStartAsync();
        _ = vm.UpscalersDailyAsync();
        if (_services.Settings.StartInBigPicture) Dispatcher.BeginInvoke(() => vm.BigPictureCommand.Execute(null), DispatcherPriority.ApplicationIdle);
        if (nxm is not null) _ = vm.HandleNxmAsync(nxm);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (MainWindow is not { IsLoaded: true })
        {
            // Failed while starting: explain, then close instead of hanging without a window.
            CrashReporter.Show(FitzLauncher.Core.Diagnostics.ErrorExplainer.Explain(e.Exception),
                FitzLauncher.Core.Diagnostics.ErrorExplainer.Report(e.Exception, CrashReporter.Version, CrashReporter.Os, "start-up"), fatal: true);
            Shutdown();
            return;
        }
        CrashReporter.ShowRecoverable(e.Exception, "while using the launcher", line => _services?.Log.Write("Unhandled: " + line));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _nxmStop.Cancel();
        if (_services is not null)
        {
            // Never leave a game frozen or the sleep overrides in place when the launcher closes.
            FitzLauncher.Windows.GameFreezer.RecoverIfNeeded(_services.Paths);
            FitzLauncher.Windows.PowerRequestOverride.Clear(_services.Paths);
            if (_services.GameMode.IsActive) _services.GameMode.Restore();
            if (!AppServices.SkipSaveOnExit)
            {
                _services.SaveSettings();
                _services.SaveLibrary();
            }
            _services.Dispose();
        }
        CrashReporter.Stop();
        if (_ownsMutex) _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
