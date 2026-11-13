using System.ComponentModel;
using System.Diagnostics;
using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;
using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.Windows;

/// <summary>
/// One game session of the closed-loop tuner (SPEC §12). Frames arrive from the ETW thread into <see cref="RollingFrameStats"/>;
/// a sampler task calls <see cref="ClosedLoopTuner.Update"/> once a second under a lock and carries out its decisions.
/// Watch only decides and logs but changes nothing. On records, applies and, at the end, puts everything back.
/// </summary>
public sealed class ClosedLoopSession
{
    private const int MaxLog = 200;
    private const int TimelineSeconds = 60;
    /// <summary>A bucket older than this is not "now": no frames have arrived for a while.</summary>
    private const double StaleBucketMs = 2500;

    private readonly ClosedLoopMode _mode;
    private readonly ClosedLoopHooks _hooks;
    private readonly ClosedLoopApplier _applier;
    private readonly RollingFrameStats _stats = new();
    private readonly ClosedLoopTuner _tuner = new();
    private readonly Lock _lock = new();
    private readonly List<ClosedLoopLogEntry> _log = [];
    private readonly Func<string?> _fpsError;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private double _lastFrameMs;
    private long _lastFrameTicks;
    private bool _framesSeen;

    /// <summary>Raised from the sampler thread once a second. Marshal to the UI thread before touching controls.</summary>
    public event Action<ClosedLoopSnapshot>? Updated;

    public ClosedLoopSession(ClosedLoopMode mode, ClosedLoopHooks hooks, ClosedLoopApplier applier, Func<string?> fpsError)
    {
        _mode = mode;
        _hooks = hooks;
        _applier = applier;
        _fpsError = fpsError;
    }

    /// <summary>Feed for <see cref="EtwFpsTracker.SetFrameListener"/>. Never throws.</summary>
    public void OnFrame(double timestampMs, double frameMs)
    {
        _stats.Add(timestampMs, frameMs);
        lock (_lock)
        {
            _lastFrameMs = timestampMs;
            _lastFrameTicks = Stopwatch.GetTimestamp();
            _framesSeen = true;
        }
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is InvalidOperationException or IOException or Win32Exception or ArgumentException or UnauthorizedAccessException or NotSupportedException)
                {
                    _hooks.Log?.Invoke("Telemetry tick failed: " + e.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* session ending */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        double nowMs;
        bool seen;
        lock (_lock)
        {
            seen = _framesSeen;
            nowMs = _lastFrameMs + (seen ? (Stopwatch.GetTimestamp() - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency : 0);
        }
        var snap = _stats.Snapshot(nowMs);
        // The newest finished one-second bucket, if it is recent enough to mean "now".
        var bucket = snap.Buckets.Count > 0 ? snap.Buckets[^1] : default;
        if (snap.Buckets.Count > 1 && nowMs - bucket.StartMs < RollingFrameStats.BucketMs) bucket = snap.Buckets[^2];
        if (snap.Buckets.Count == 0 || nowMs - (bucket.StartMs + RollingFrameStats.BucketMs) > StaleBucketMs) bucket = default;

        var (cpu, gpu) = _hooks.Temperatures();
        var ctx = _hooks.Context();
        var now = DateTime.UtcNow;
        List<TunerAction> actions;
        IReadOnlyList<TunerRuleStatus> rules;
        lock (_lock)
        {
            actions = [.. _tuner.Update(new TunerSample(bucket, cpu, gpu, ctx), now)];
            rules = _tuner.Status(now);
        }

        foreach (var a in actions) await HandleAsync(a, ct).ConfigureAwait(false);

        var busyAvailable = snap.Window.MedianCpuBusyMs is not null && snap.Window.MedianGpuBusyMs is not null;
        List<ClosedLoopLogEntry> log;
        lock (_lock) log = [.. _log];
        Updated?.Invoke(new ClosedLoopSnapshot(
            _mode, true, bucket.AvgFps, snap.Window.Frames > 0 ? snap.Window.OnePercentLowFps : 0,
            snap.Window.MedianCpuBusyMs, snap.Window.MedianGpuBusyMs, cpu, gpu,
            [.. snap.Buckets.TakeLast(TimelineSeconds).Select(b => (float)b.AvgFps)],
            rules, ClosedLoopAdvice.WhyNotRunning(_mode, true, _fpsError(), seen && snap.HasData, busyAvailable), log));
    }

    private async Task HandleAsync(TunerAction a, CancellationToken ct)
    {
        var applied = false;
        // Watch only never changes anything. Notes and Advise-only decisions have nothing to change either.
        if (_mode == ClosedLoopMode.On && a.Type != TunerActionType.Note)
        {
            try { applied = await Task.Run(() => _applier.Apply(a), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidOperationException or IOException or Win32Exception or ArgumentException or UnauthorizedAccessException)
            {
                _hooks.Log?.Invoke("Telemetry action failed: " + e.Message);
            }
        }
        AddLog(a, applied);
    }

    private void AddLog(TunerAction a, bool applied)
    {
        var prefix = _mode == ClosedLoopMode.WatchOnly && a.Type != TunerActionType.Note ? "Would: " : "";
        lock (_lock)
        {
            _log.Add(new ClosedLoopLogEntry(DateTimeOffset.Now, a.Rule, a.Type, prefix + a.Text, applied));
            if (_log.Count > MaxLog) _log.RemoveAt(0);
        }
        _hooks.Log?.Invoke($"Telemetry {a.Rule?.ToString() ?? "note"} {a.Type}: {a.Text}");
    }

    /// <summary>The Revert button: puts one rule back and keeps it from acting again this session.</summary>
    public async Task RevertAsync(TunerRule rule, CancellationToken ct)
    {
        TunerAction? action;
        lock (_lock) action = _tuner.Revert(rule);
        if (action is not null) await HandleAsync(action, ct).ConfigureAwait(false);
    }

    /// <summary>The game ended: stops the sampler, puts everything back and removes the restore file.</summary>
    public async Task EndAsync()
    {
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        List<TunerAction> ending;
        lock (_lock) ending = [.. _tuner.EndSession()];
        foreach (var a in ending) AddLog(a, _mode == ClosedLoopMode.On && _applier.Apply(a));
        if (_mode == ClosedLoopMode.On) _applier.Finish();
        _cts?.Dispose();
        _cts = null;
        List<ClosedLoopLogEntry> log;
        lock (_lock) log = [.. _log];
        Updated?.Invoke(ClosedLoopSnapshot.Idle with { Mode = _mode, Log = log,
            WhyNotRunning = "The game has ended. The log above is from that session." });
    }
}
