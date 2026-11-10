namespace FitzLauncher.Core.Input;

/// <summary>
/// Turns "which buttons are down right now" (polled ~60 times a second) into clean presses for menus.
/// A pad can reach the launcher by two routes (XInput and DualShock HID) that report the same press a few
/// milliseconds apart, and contacts can chatter, so a button only counts as released once it has been up for
/// <see cref="ReleaseGraceMs"/>. A D-pad hat shows a diagonal for a moment while the thumb rolls from one
/// direction to the next, so a second direction arriving within <see cref="SettleMs"/> of the first is ignored.
/// </summary>
public sealed class PressFilter<T>(Func<T, bool> repeats, Func<T, bool> isDirection) where T : notnull
{
    public const int ReleaseGraceMs = 80;
    public const int SettleMs = 80;
    public const int FirstRepeatMs = 380;
    public const int RepeatMs = 110;

    private readonly Dictionary<T, DateTime> _next = [];
    private readonly Dictionary<T, DateTime> _lastSeen = [];
    private T? _lastDir;
    private DateTime _lastDirAt = DateTime.MinValue;

    /// <returns>The buttons to act on this tick (new presses, plus repeats for held directions and scrolling).</returns>
    public List<T> Step(IReadOnlyCollection<T> down, DateTime now)
    {
        var fired = new List<T>();
        foreach (var b in down)
        {
            _lastSeen[b] = now;
            if (!_next.TryGetValue(b, out var next))
            {
                _next[b] = now.AddMilliseconds(FirstRepeatMs);
                if (isDirection(b))
                {
                    if (_lastDir is { } last && !EqualityComparer<T>.Default.Equals(last, b) && (now - _lastDirAt).TotalMilliseconds < SettleMs) continue;
                    _lastDir = b;
                    _lastDirAt = now;
                }
                fired.Add(b);
            }
            else if (now >= next && repeats(b))
            {
                fired.Add(b);
                _next[b] = now.AddMilliseconds(RepeatMs);
            }
        }
        foreach (var b in _next.Keys.ToList())
            if (!down.Contains(b) && (now - _lastSeen[b]).TotalMilliseconds >= ReleaseGraceMs)
            {
                _next.Remove(b);
                _lastSeen.Remove(b);
            }
        return fired;
    }
}
