using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Thermal guard: does nothing until the chip is genuinely hot. When the hotter of CPU and GPU (one die on an APU)
/// stays at or above the trigger temperature (default 90°C) for a while, the frame cap drops one small step
/// (117 → 110 → 100 → 90 → 80 → 72 → 60). Between the trigger and a few degrees below it, it holds so temperatures
/// can level out; once they've been below that for a while it steps back up, one step at a time.
/// Pure state machine, so it can be tested with a fake clock.
/// </summary>
public sealed class ThermalGovernor
{
    private static readonly int[] Ladder = [141, 117, 110, 100, 90, 80, 72, 60];
    private readonly ThermalGuardOptions _o;
    private readonly double _trigger;
    private readonly int[] _steps;
    private int _index;
    private DateTime? _hotSince, _coolSince;
    private DateTime _episodeStart;
    private double _episodeTemp;
    private bool _futile;

    /// <param name="triggerC">Temperature that starts the guard (the setting, lowered by cooler modes).</param>
    public ThermalGovernor(int originalCap, ThermalGuardOptions options, double triggerC)
    {
        _o = options;
        _trigger = triggerC;
        _steps = new[] { originalCap }.Concat(Ladder.Where(l => l < originalCap && l >= options.MinimumCap)).ToArray();
    }

    public int OriginalCap => _steps[0];
    public int CurrentCap => _steps[_index];
    public bool IsThrottling => _index > 0;
    public double TriggerC => _trigger;

    /// <summary>
    /// Feed a reading; returns the new cap when it changes, otherwise null. <paramref name="allowStepDown"/> is false
    /// while something better is cooling the chip (auto TDP lowering power): the cap then stays put, though it can still recover.
    /// If a lower cap isn't bringing the temperature down (the heat isn't coming from the frame rate, e.g. a menu), it
    /// goes back up a step and stops asking: running slower for nothing is the worst of both.
    /// </summary>
    public int? Update(double? cpuC, double? gpuC, DateTime now, bool allowStepDown = true)
    {
        var temp = Math.Max(cpuC ?? 0, gpuC ?? 0);
        if (temp <= 0) return null; // no reading
        var hot = temp >= _trigger;
        var cool = temp <= _trigger - _o.ReleaseBelowC;

        if (hot)
        {
            _coolSince = null;
            _hotSince ??= now;
            // Capped for a fair while (three step intervals) and it's no cooler than when it started: the frame rate isn't
            // where the heat comes from. Put the cap back and stop asking, until it has cooled down.
            if (_index > 0 && !_futile && (now - _episodeStart).TotalSeconds >= 3 * _o.StepDownAfterSeconds && temp > _episodeTemp - 1.5)
            {
                _futile = true;
                _index = 0;
                return CurrentCap;
            }
            if (allowStepDown && !_futile && (now - _hotSince.Value).TotalSeconds >= _o.StepDownAfterSeconds && _index < _steps.Length - 1)
            {
                if (_index == 0) { _episodeStart = now; _episodeTemp = temp; }
                _index++;
                _hotSince = now; // give the lower cap time to work before another step
                return CurrentCap;
            }
            return null;
        }

        _hotSince = null;
        if (cool) _futile = false;
        if (!cool || _index == 0) { _coolSince = null; return null; } // levelling out: hold
        _coolSince ??= now;
        if ((now - _coolSince.Value).TotalSeconds >= _o.RecoverAfterSeconds)
        {
            _index--;
            _coolSince = now;
            return CurrentCap;
        }
        return null;
    }
}
