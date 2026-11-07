using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

public readonly record struct LiveStats(double Fps, double? CpuTempC, double? GpuTempC, double? GpuLoad, TimeSpan Elapsed);
public readonly record struct TempAlert(string Component, double TempC, double ThresholdC);

/// <summary>
/// One play session from launch to exit: Game Mode on → start game → track processes, FPS and
/// temperatures → Game Mode off → write a compact session summary.
/// </summary>
public sealed class GameSession(
    Game game,
    LauncherSettings settings,
    HardwareProfile? hardware,
    GameModeController gameMode,
    TemperatureMonitor temps,
    LibraryStore store)
{
    public event Action<string>? Status;
    public event Action<LiveStats>? Live;
    public event Action<TempAlert>? Alert;

    public async Task<SessionSummary> RunAsync(bool launchAsAdmin, CancellationToken ct)
    {
        var useGameMode = settings.GameMode.Enabled && game.UseGameMode;
        var tuning = SystemAdvisor.Tune(hardware);
        var installDir = GameScanner.ResolveInstallRoot(game);

        if (useGameMode)
        {
            Status?.Invoke("Game Mode: preparing…");
            foreach (var line in await Task.Run(() => gameMode.ActivatePreLaunch(settings.GameMode, tuning), ct)) Status?.Invoke(line);
        }

        var start = DateTimeOffset.Now;
        int pid;
        try
        {
            var args = $"{game.Arguments} {game.OptimizerArguments}".Trim();
            pid = GameLauncher.Start(game.ExePath, args, launchAsAdmin);
        }
        catch
        {
            if (useGameMode) gameMode.Restore();
            throw;
        }
        Status?.Invoke($"Started {game.Name}");

        var tracker = new GameProcessTracker(pid, installDir);
        tracker.Refresh();
        using var fps = new EtwFpsTracker();
        if (settings.TrackFps && !fps.Start(tracker.Contains) && fps.Error is { } err) Status?.Invoke(err);

        // The slower Game Mode steps run while the game is loading.
        if (useGameMode)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            tracker.Refresh();
            foreach (var line in await gameMode.ActivatePostLaunchAsync(settings.GameMode, tuning, tracker.Pids, ct)) Status?.Invoke(line);
        }
        MemoryCleaner.TrimSelf();

        // ---------------------------------------------------------------- monitor loop
        double? maxCpu = null, maxGpu = null, sumCpu = 0, sumGpu = 0;
        int cpuSamples = 0, gpuSamples = 0, alerts = 0, emptyPolls = 0, tick = 0;
        DateTime? cpuHotSince = null, gpuHotSince = null, lastCpuAlert = null, lastGpuAlert = null;
        var priorityRaised = new HashSet<int>(tracker.Pids);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                tick++;
                // Process list every 4 s; sensors every 2 s.
                if (tick % 2 == 0)
                {
                    var alive = tracker.Refresh();
                    var graceOver = DateTimeOffset.Now - start > TimeSpan.FromSeconds(30);
                    emptyPolls = alive == 0 ? emptyPolls + 1 : 0;
                    if (emptyPolls >= 2 && graceOver) break;
                    if (useGameMode && settings.GameMode.RaiseGamePriority)
                    {
                        var fresh = tracker.Pids.Where(priorityRaised.Add).ToList();
                        if (fresh.Count > 0) GameModeController.RaiseGamePriority(fresh);
                    }
                }

                var r = temps.Read();
                if (r.CpuTempC is { } c) { maxCpu = Math.Max(maxCpu ?? 0, c); sumCpu += c; cpuSamples++; }
                if (r.GpuTempC is { } g) { maxGpu = Math.Max(maxGpu ?? 0, g); sumGpu += g; gpuSamples++; }

                if (CheckSustained(r.CpuTempC, settings.CpuTempAlertC, ref cpuHotSince, ref lastCpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("CPU", r.CpuTempC!.Value, settings.CpuTempAlertC)); }
                if (CheckSustained(r.GpuTempC, settings.GpuTempAlertC, ref gpuHotSince, ref lastGpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("GPU", r.GpuTempC!.Value, settings.GpuTempAlertC)); }

                Live?.Invoke(new LiveStats(fps.CurrentFps, r.CpuTempC, r.GpuTempC, r.GpuLoad, DateTimeOffset.Now - start));
            }
        }
        catch (OperationCanceledException) { /* launcher closing */ }

        // ---------------------------------------------------------------- wrap up
        var end = DateTimeOffset.Now;
        fps.Stop();
        if (useGameMode)
        {
            foreach (var line in gameMode.Restore()) Status?.Invoke(line);
        }

        var recorder = fps.PrimaryRecorder ?? new FrameTimeRecorder();
        var summary = recorder.Summarise(game, start, end, game.FpsTarget);
        summary.MaxCpuTempC = maxCpu;
        summary.MaxGpuTempC = maxGpu;
        summary.AvgCpuTempC = cpuSamples > 0 ? sumCpu / cpuSamples : null;
        summary.AvgGpuTempC = gpuSamples > 0 ? sumGpu / gpuSamples : null;
        summary.TempAlerts = alerts;

        store.AppendSession(summary);
        return summary;

        bool CheckSustained(double? value, double threshold, ref DateTime? hotSince, ref DateTime? lastAlert)
        {
            if (value is not { } v || v < threshold) { hotSince = null; return false; }
            hotSince ??= DateTime.Now;
            if ((DateTime.Now - hotSince.Value).TotalSeconds < settings.TempAlertSustainSeconds) return false;
            if (lastAlert is { } la && (DateTime.Now - la).TotalMinutes < 2) return false;
            lastAlert = DateTime.Now;
            return true;
        }
    }
}
