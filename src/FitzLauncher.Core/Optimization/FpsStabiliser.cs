namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Smoothness mode: frame pacing feels best when the frame rate never moves, so this holds the cap at the highest
/// rate the game can actually sustain right now. When the frame rate keeps dipping well under the cap (a heavy
/// area, heat), the cap drops to a level the game holds steadily; once there's clear headroom for long enough it
/// steps back up, never above the cap you chose. Hysteresis and hold times stop it from flapping.
/// </summary>
public sealed class FpsStabiliser
{
    /// <summary>Rungs it can settle on (plus the user's own cap).</summary>
    public static readonly int[] Ladder = [40, 45, 50, 55, 60, 72, 80, 90, 100, 110, 117, 141];

    private readonly Queue<(DateTime at, double fps)> _window = new();
    private readonly TimeSpan _span;
    private readonly TimeSpan _downHold, _upHold;
    private DateTime _lastChange = DateTime.MinValue;
    private DateTime? _headroomSince;

    public int Ceiling { get; private set; }
    /// <summary>The cap it's holding now (equals <see cref="Ceiling"/> when the game keeps up).</summary>
    public int Current { get; private set; }
    public bool Lowered => Current < Ceiling;

    public FpsStabiliser(int ceiling, TimeSpan? window = null, TimeSpan? downHold = null, TimeSpan? upHold = null)
    {
        Ceiling = Current = ceiling;
        _span = window ?? TimeSpan.FromSeconds(12);
        _downHold = downHold ?? TimeSpan.FromSeconds(8);
        _upHold = upHold ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>The user changed their cap (quick switch, screen change): start again from it.</summary>
    public void SetCeiling(int ceiling)
    {
        Ceiling = Current = ceiling;
        _window.Clear();
        _headroomSince = null;
    }

    /// <summary>After sleep, a loading screen or anything that makes old samples meaningless.</summary>
    public void Reset() { _window.Clear(); _headroomSince = null; }

    /// <summary>Feed the current FPS (every couple of seconds). Returns a new cap when it should change, else null.</summary>
    public int? Update(double fps, DateTime now, double? gpuLoad = null)
    {
        if (Ceiling <= 0 || fps <= 1) return null; // paused, minimised, loading
        _window.Enqueue((now, fps));
        while (_window.Count > 0 && now - _window.Peek().at > _span) _window.Dequeue();
        if (_window.Count < 4) return null;

        var ordered = _window.Select(x => x.fps).Order().ToList();
        var low = ordered[(int)Math.Floor((ordered.Count - 1) * 0.2)]; // the dips, not one-off spikes
        var held = now - _lastChange;

        // Down: most of the window sits clearly under the cap.
        if (low < Current * 0.93 && held >= _downHold)
        {
            var target = Rungs().Where(r => r < Current && r <= low * 0.99).DefaultIfEmpty(Rungs().First()).Max();
            if (target < Current) return Change(target, now);
        }

        // Up: the game held the current cap with room to spare (and the GPU isn't maxed) for a while.
        var next = Rungs().Where(r => r > Current).DefaultIfEmpty(0).Min();
        var headroom = next > 0 && low >= Current * 0.985 && (gpuLoad is null || gpuLoad < 88);
        if (!headroom) { _headroomSince = null; return null; }
        _headroomSince ??= now;
        if (now - _headroomSince >= _upHold && held >= _upHold) return Change(next, now);
        return null;
    }

    private int Change(int cap, DateTime now)
    {
        Current = cap;
        _lastChange = now;
        _headroomSince = null;
        _window.Clear();
        return cap;
    }

    private IEnumerable<int> Rungs() => Ladder.Where(r => r < Ceiling).Append(Ceiling).Order();
}
