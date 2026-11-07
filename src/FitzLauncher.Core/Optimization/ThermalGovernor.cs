using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Thermal guard: when the CPU or GPU sits near its limit, step the frame cap down one notch
/// (117 → 110 → 100 → 90 → 75 → 60). Fewer frames means less heat, and a lower steady cap feels
/// smoother than the hardware throttling itself. Steps back up once things have cooled down.
/// Pure state machine, so it can be tested with a fake clock.
/// </summary>
public sealed class ThermalGovernor
{
    private static readonly int[] Ladder = [117, 110, 100, 90, 75, 60];
    private readonly ThermalGuardOptions _o;
    private readonly double _cpuLimit, _gpuLimit;
    private readonly int[] _steps;
    private int _index;
    private DateTime? _hotSince, _coolSince;

    public ThermalGovernor(int originalCap, ThermalGuardOptions options, double cpuLimitC, double gpuLimitC)
    {
        _o = options;
        _cpuLimit = cpuLimitC;
        _gpuLimit = gpuLimitC;
        _steps = new[] { originalCap }.Concat(Ladder.Where(l => l < originalCap && l >= options.MinimumCap)).ToArray();
    }

    public int OriginalCap => _steps[0];
    public int CurrentCap => _steps[_index];
    public bool IsThrottling => _index > 0;

    /// <summary>Feed a reading; returns the new cap when it changes, otherwise null.</summary>
    public int? Update(double? cpuC, double? gpuC, DateTime now)
    {
        var hot = cpuC >= _cpuLimit - _o.MarginC || gpuC >= _gpuLimit - _o.MarginC;
        var cool = (cpuC ?? 0) < _cpuLimit - _o.RecoverMarginC && (gpuC ?? 0) < _gpuLimit - _o.RecoverMarginC;

        if (hot)
        {
            _coolSince = null;
            _hotSince ??= now;
            if ((now - _hotSince.Value).TotalSeconds >= _o.StepDownAfterSeconds && _index < _steps.Length - 1)
            {
                _index++;
                _hotSince = now; // wait again before the next step down
                return CurrentCap;
            }
            return null;
        }

        _hotSince = null;
        if (!cool || _index == 0) { _coolSince = null; return null; }
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
