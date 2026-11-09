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

    /// <summary>Feed a reading; returns the new cap when it changes, otherwise null.</summary>
    public int? Update(double? cpuC, double? gpuC, DateTime now)
    {
        var temp = Math.Max(cpuC ?? 0, gpuC ?? 0);
        if (temp <= 0) return null; // no reading
        var hot = temp >= _trigger;
        var cool = temp <= _trigger - _o.ReleaseBelowC;

        if (hot)
        {
            _coolSince = null;
            _hotSince ??= now;
            if ((now - _hotSince.Value).TotalSeconds >= _o.StepDownAfterSeconds && _index < _steps.Length - 1)
            {
                _index++;
                _hotSince = now; // give the lower cap time to work before another step
                return CurrentCap;
            }
            return null;
        }

        _hotSince = null;
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
