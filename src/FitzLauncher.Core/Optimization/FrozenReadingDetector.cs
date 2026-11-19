namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Spots a sensor source that has stopped updating. Live temperature, power and load readings never repeat exactly for
/// long; from a real Witcher 3 log the same CPU/GPU temperatures, package power and loads were logged unchanged for 270 s
/// while the game stalled and the PC then froze. Acting on those (82 °C "hot" held for minutes, then a 0.1 W "draw" read
/// as room to spare) is worse than having no reading.
/// </summary>
public sealed class FrozenReadingDetector(int repeatsToFreeze = 8)
{
    private double?[]? _last;
    private int _same;

    /// <summary>Feed one reading set; true once it has been identical (and not all empty) for <c>repeatsToFreeze</c> reads in a row.</summary>
    public bool Update(params double?[] values)
    {
        if (values.All(v => v is null)) { _last = null; _same = 0; return false; }
        if (_last is not null && _last.AsSpan().SequenceEqual(values)) _same++;
        else { _last = (double?[])values.Clone(); _same = 1; }
        return _same >= repeatsToFreeze;
    }
}
