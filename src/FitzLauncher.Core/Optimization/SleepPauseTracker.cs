namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Pause-on-sleep bookkeeping for one session. Screen off / sleep → pause once; wake → begin resuming
/// (the caller waits for sign-in); going back to sleep while waiting cancels the resume. Time spent
/// asleep is added up so it isn't counted as play time.
/// </summary>
public sealed class SleepPauseTracker
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _pausedAt;
    private bool _resuming;

    public TimeSpan Asleep { get; private set; }
    public int Pauses { get; private set; }

    public bool IsPaused { get { lock (_lock) return _pausedAt is not null; } }
    public bool IsResuming { get { lock (_lock) return _resuming; } }

    /// <summary>True when the game should be paused now (not already paused).</summary>
    public bool TryPause(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_pausedAt is not null) { _resuming = false; return false; } // back to sleep mid-wake
            _pausedAt = now;
            _resuming = false;
            Pauses++;
            return true;
        }
    }

    /// <summary>True when a resume should start (paused, and no resume already waiting).</summary>
    public bool TryBeginResume()
    {
        lock (_lock)
        {
            if (_pausedAt is null || _resuming) return false;
            _resuming = true;
            return true;
        }
    }

    /// <summary>Marks the game running again; returns how long it was paused.</summary>
    public TimeSpan? CompleteResume(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_pausedAt is not { } at) return null;
            var d = now - at;
            if (d > TimeSpan.Zero) Asleep += d;
            _pausedAt = null;
            _resuming = false;
            return d;
        }
    }
}
