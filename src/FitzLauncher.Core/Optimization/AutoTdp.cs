namespace FitzLauncher.Core.Optimization;

public enum AutoTdpMode { Off, Observe, On }

/// <summary>What auto TDP learned for one game at one FPS target: the power range that held it.</summary>
public sealed class AutoTdpProfile
{
    /// <summary>Lowest limit that held the target for a good while.</summary>
    public int Min { get; set; }
    /// <summary>The limit it spent most of its healthy time at (where the next session starts, plus a little).</summary>
    public int Typical { get; set; }
    /// <summary>Highest limit it had to use (big scenes).</summary>
    public int Max { get; set; }
    public int Sessions { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;

    public string Text => $"{Min}–{Max} W, usually {Typical} W ({Sessions} session{(Sessions == 1 ? "" : "s")})";

    /// <summary>Blends a new session in (older sessions count for more as they pile up).</summary>
    public static AutoTdpProfile Blend(AutoTdpProfile? old, AutoTdpProfile now)
    {
        if (old is null || old.Sessions == 0) return new AutoTdpProfile { Min = now.Min, Typical = now.Typical, Max = now.Max, Sessions = 1 };
        var w = Math.Min(old.Sessions, 4);
        int Mix(int a, int b) => (int)Math.Round((a * w + b) / (double)(w + 1));
        return new AutoTdpProfile { Min = Mix(old.Min, now.Min), Typical = Mix(old.Typical, now.Typical), Max = Math.Max(old.Max * 3 / 4 + now.Max / 4, now.Max), Sessions = old.Sessions + 1 };
    }
}

/// <summary>One look at the game: the last few seconds of frames, and what the chip is doing.</summary>
/// <param name="Frames">Frames in the window (a menu or loading screen has too few to judge).</param>
/// <param name="Fps">Average FPS over the window.</param>
/// <param name="LowFps">1% low over the window.</param>
/// <param name="DrawnWatts">Power the chip is drawing (null when the sensor can't tell).</param>
/// <param name="TempC">The hotter of CPU and GPU.</param>
/// <param name="ThermalHold">The thermal guard is cooling things down: leave power alone.</param>
public readonly record struct AutoTdpSample(int Frames, double Fps, double LowFps, double? DrawnWatts, double? TempC = null, bool ThermalHold = false);

/// <summary>What the governor wants: a new limit, or just an explanation (Watts null).</summary>
public sealed record AutoTdpDecision(int? Watts, string Reason);

/// <summary>
/// Auto TDP: holds the FPS target with the least power that stays smooth.
/// <list type="bullet">
/// <item><b>Up fast.</b> If frames dip (1% low under 90 % of the target, or average under 95 %) it adds power straight away:
/// 5 W, 8 W for a bad dip, 10 W when more than 30 % under the target. If the power reading says the chip isn't at its limit
/// the reading can be wrong (some chips under-report), so it tests with a raise instead of trusting it. If extra watts don't
/// improve FPS, it goes back, stops asking for more for a while, and only then trusts the reading enough to trim the limit.</item>
/// <item><b>Down slowly.</b> With the target held comfortably (1% low over 96 %) it first snaps the limit to just above what the
/// game really draws (peak of the last minute + 4 W), then lowers 2 W at a time after 40 s of room to spare. It never goes below
/// a level that just dipped (+3 W margin, remembered for 10 min), and the level a big scene needed fades only 1 W per 20 s, so
/// fights and big outdoor areas don't catch it low.</item>
/// <item><b>Safe.</b> Always inside min–max, hands off while the thermal guard is acting or it's very hot.</item>
/// </list>
/// In <c>simulated</c> (observe) mode nothing is applied: the limit it would hold is tracked against what the game really draws.
/// Pure state machine, tested with a fake clock.
/// </summary>
public sealed class AutoTdpGovernor
{
    // A low 1% low alone (average already above the target) is a hitch (loading, streaming), not a power shortage: extra watts don't fix it
    // and cost heat. From a real log: two +8 W raises to 57 W for hitches at 117 FPS against a 110 target.
    private const double SmoothFloor = 0.90, AvgFloor = 0.95, RoomLow = 0.94, RoomAvg = 0.98;
    // At the FPS cap the average can't rise above the target, so a ragged 1% low there looks like a shortage forever. Once extra watts have
    // been tried and didn't help, a 1% low down to 60 % of the target with the average on the target counts as hitching (not power) for 10 minutes.
    // From a real log: 14 minutes at 55 W holding a 100 FPS cap, 1% low 60-98, never trimmed; the same scene was smooth at 44-49 W.
    private const double HitchAvg = 0.98, HitchLow = 0.60;
    // With FPS collapsed, a reading this far over the trigger means heat is the cause, not a power shortage. The CPU can jump 85 → 94 °C in
    // a few seconds and the 16 s average lags it: from a real log, it added 5 W at 91 °C and held 6 s more before cutting; FPS sat at 34 for 20 s.
    private const double CollapseHeatC = 3;
    private static readonly TimeSpan HitchMemory = TimeSpan.FromMinutes(10);
    private DateTime _hitchProvenAt = DateTime.MinValue;

    private int _target;
    private readonly int _min, _max;
    private readonly bool _simulated;
    private readonly double _trigger;
    private bool _hotEpisode;
    private readonly Queue<(DateTime at, double w)> _drawn = [];
    private readonly Dictionary<int, double> _levelSeconds = [];
    private DateTime? _last, _healthySince;
    private DateTime _lastChange = DateTime.MinValue, _floorDecayAt = DateTime.MinValue, _ceilingUntil = DateTime.MinValue;
    private (int w, DateTime at)? _failed;
    private (DateTime at, double fps, double low, int prev, bool probe)? _gain;
    private int _gainFails;
    /// <summary>A raise helped while the power reading said the limit wasn't binding: the reading under-reports, so it is no longer used to hold power back or to trim.</summary>
    private bool _drawUntrusted;
    private readonly int? _knownTypical;
    private readonly Queue<(DateTime at, double c)> _temps = [];
    private int _needLevel;
    private DateTime _needAt;
    private int _floor, _ceiling;
    private int _coolCeiling;
    /// <summary>The limit when the current heat episode began: cuts stay within 12 W of it unless it gets very hot, and the climb back goes to near it.</summary>
    private int _hotStart;
    private DateTime _coolCeilingUntil = DateTime.MinValue;
    private string? _lastNote;
    private double _seconds, _limitSeconds, _drawnSeconds, _drawnTotal, _healthySeconds;

    /// <param name="thermalTriggerC">The temperature (the thermal guard's trigger) above which it lowers power to cool the chip.</param>
    public AutoTdpGovernor(int target, int minWatts, int maxWatts, int startWatts, bool simulated = false, double thermalTriggerC = 90, int? learnedTypical = null)
    {
        _knownTypical = learnedTypical;
        (_target, _min, _max, _simulated, _trigger) = (target, minWatts, Math.Max(maxWatts, minWatts), simulated, thermalTriggerC);
        Current = Math.Clamp(startWatts, _min, _max);
        _floor = _min;
        _ceiling = _max;
        MaxUsed = Current;
    }

    public int Current { get; private set; }
    /// <summary>The FPS the governor is holding the game to right now.</summary>
    public int Target => _target;

    /// <summary>The limit it asked for was not applied (RyzenAdj failed or timed out): go back to what is really set and try again after a pause.</summary>
    public void Resync(int appliedWatts)
    {
        Current = Math.Clamp(appliedWatts, _min, _max);
        _gain = null;
    }

    /// <summary>The FPS the game is being held to right now (the cap can change: quick switch, thermal guard).</summary>
    public void SetTarget(int target) { if (target > 0) _target = target; }

    /// <summary>Power can still come down to cool the chip, so the FPS-cap guard should hold off (real mode only).</summary>
    public bool CanCoolByPower => !_simulated && Current > _min;
    public int MaxUsed { get; private set; }
    public double PeakDrawn { get; private set; }
    public int Changes { get; private set; }
    /// <summary>Time-weighted average limit it held (observe mode: what it would have averaged).</summary>
    public double AverageLimit => _seconds > 0 ? _limitSeconds / _seconds : Current;
    public double? AverageDrawn => _drawnSeconds > 0 ? _drawnTotal / _drawnSeconds : null;

    /// <summary>The power range that held the target this session; null until there's a minute of healthy play.</summary>
    public AutoTdpProfile? Summary()
    {
        if (_healthySeconds < 60) return null;
        var levels = _levelSeconds.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
        var solid = levels.Where(kv => kv.Value >= 30).Select(kv => kv.Key).ToList();
        double half = levels.Sum(kv => kv.Value) / 2, run = 0;
        var typical = levels.Last().Key;
        foreach (var kv in levels) { run += kv.Value; if (run >= half) { typical = kv.Key; break; } }
        return new AutoTdpProfile { Min = solid.Count > 0 ? solid.Min() : typical, Typical = typical, Max = Math.Max(MaxUsed, typical), Sessions = 1 };
    }

    public AutoTdpDecision? Update(AutoTdpSample s, DateTime now)
    {
        var dt = _last is { } l ? Math.Clamp((now - l).TotalSeconds, 0, 10) : 0;
        _last = now;
        if (s.Frames < 20) return null; // menu, loading or paused: nothing to judge

        _seconds += dt;
        _limitSeconds += dt * Current;
        double? peak60 = null;
        if (s.DrawnWatts is { } w)
        {
            _drawnSeconds += dt; _drawnTotal += dt * w; PeakDrawn = Math.Max(PeakDrawn, w);
            _drawn.Enqueue((now, w));
        }
        while (_drawn.Count > 0 && now - _drawn.Peek().at > TimeSpan.FromSeconds(60)) _drawn.Dequeue();
        if (_drawn.Count > 0) peak60 = _drawn.Max(x => x.w);

        var binding = _drawUntrusted || s.DrawnWatts is not { } d || d >= Current - (_simulated ? 1.5 : 2.5); // unknown draw: assume the limit is what's holding it
        var distress = _simulated ? s.DrawnWatts is { } sd && sd >= Current - 1.5 : (s.LowFps < _target * SmoothFloor && s.Fps < _target * 1.03) || s.Fps < _target * AvgFloor;
        var hitchOnly = !_simulated && now - _hitchProvenAt < HitchMemory && s.Fps >= _target * HitchAvg && s.LowFps >= _target * HitchLow;
        if (hitchOnly) { distress = false; _hitchProvenAt = now; } // refreshed while the pattern lasts, so it can't lapse mid-trim and raise power again
        // Far under the target (a loading hitch aside, something is badly wrong): power must not be the thing that is held back.
        var severe = !_simulated && s.Fps < _target * 0.60;
        if (!distress) { _healthySeconds += dt; _levelSeconds[Current] = _levelSeconds.GetValueOrDefault(Current) + dt; }

        // Too hot: the fix that costs the least is less power (the frame cap stays put). Stop once it's below the trigger.
        // CPU temperature swings 10-20 °C in a few seconds (boost bursts, the fan stepping up and down) and the cooling reacts slowly.
        // Reacting to single readings cut power, which dipped FPS, which raised power again. So heat is judged on the last ~16 s;
        // only a reading far past the trigger acts alone.
        var temp = s.TempC ?? 0;
        if (temp > 0) _temps.Enqueue((now, temp));
        while (_temps.Count > 0 && now - _temps.Peek().at > TimeSpan.FromSeconds(16)) _temps.Dequeue();
        var smooth = _temps.Count > 0 ? _temps.Average(x => x.c) : 0;
        var hot = temp > 0 && (smooth >= _trigger || temp >= _trigger + 8);
        var warm = temp > 0 && smooth >= _trigger - 5;
        var deepCool = smooth > 0 && smooth < _trigger - 10; // clearly cool: the chip has room, so heat-related holds no longer apply
        if (hot)
        {
            if (!_hotEpisode) _hotStart = Current;
            _hotEpisode = true;
            _healthySince = null;
            if (severe && smooth < _trigger + 2 && temp < _trigger + CollapseHeatC)
                return Note($"{smooth:0} °C but FPS is only {s.Fps:0}: not cutting power further, that would only make it worse");
            if (severe && Current > _min)
            {
                // FPS has collapsed (about 34) while the chip is 2 °C+ over the trigger. In the logs this state ends only when the chip
                // cools below about 80 °C, and cutting is what cools it: 5 W every 12 s took 20 s+; cut harder and sooner, then let it climb back.
                if ((now - _lastChange).TotalSeconds < 5) return null;
                return Change(Math.Max(_min, Current - 8), now, raised: false, $"FPS collapsed to {s.Fps:0} at {smooth:0} °C: cutting hard to cool it, power comes back as soon as it is cooler");
            }
            if (Current > _min)
            {
                // The chip reacts to a cut over 20-30 s. Cutting every 12 s drove the limit from 47 W to 26 W in a log, long after the heat had
                // already turned; so cuts are 20 s apart (12 s when very hot) and stay within 12 W of where the heat started unless it is very hot.
                var veryHot = smooth >= _trigger + 6;
                if ((now - _lastChange).TotalSeconds < (veryHot ? 12 : 20)) return null;
                var floor = veryHot ? _min : Math.Max(_min, _hotStart - 12);
                if (Current <= floor) return Note($"{smooth:0} °C: already {_hotStart - Current} W below where the heat started, waiting for it to cool");
                var next = Math.Max(floor, Current - (smooth >= _trigger + 4 ? 5 : 3));
                var cut = Change(next, now, raised: false, $"{smooth:0} °C: lowering power to cool it, keeping the FPS target");
                _healthySince = null;
                return cut;
            }
            return Note($"{temp:0} °C at the {_min} W floor: the thermal guard eases the FPS cap instead");
        }
        if (_hotEpisode && temp > 0 && smooth < _trigger - 3)
        {
            // Cooled down: don't climb straight back to the power that heated it. But the ceiling follows where the heat started (a little
            // under it), not where the cuts ended: "+4 W above the 26 W floor" held a cool chip at 30 W for 4 minutes at 90 FPS.
            _hotEpisode = false;
            _coolCeiling = Math.Min(_max, Math.Max(Current + 4, _hotStart - 5));
            _coolCeilingUntil = now.AddMinutes(5);
        }

        // Did the last raise actually help? Judged on the average OR the 1% low: the average over the last few seconds can still
        // hold menu or loading frames from before the raise, which made a real improvement look like a loss.
        if (_gain is { } g && now >= g.at)
        {
            _gain = null;
            var helped = s.Fps >= g.fps * 1.02 || s.LowFps >= g.low * 1.03;
            if (distress && !helped)
            {
                _gainFails++;
                if (s.Fps >= _target * HitchAvg) _hitchProvenAt = now; // on target and more watts changed nothing: the 1% low is hitching, not a power shortage
                var holdSeconds = Math.Min(120, 30 * (1 << Math.Min(_gainFails - 1, 3))); // 30, 60, 120 s: retried soon, never parked for minutes
                _ceiling = g.prev; _ceilingUntil = now.AddSeconds(holdSeconds);
                return Change(g.prev, now, raised: false, $"extra watts didn't improve FPS ({s.Fps:0}, 1% low {s.LowFps:0}); back to {g.prev} W, trying again in {holdSeconds} s");
            }
            if (helped)
            {
                _gainFails = 0;
                if (g.probe && (s.Fps >= g.fps * 1.05 || s.LowFps >= g.low * 1.08)) _drawUntrusted = true;
            }
        }

        var ceiling = now < _ceilingUntil ? _ceiling : _max;
        if (now < _coolCeilingUntil && !severe) ceiling = Math.Min(ceiling, _coolCeiling); // after a heat episode; not while FPS has collapsed
        if (distress)
        {
            _healthySince = null;
            if ((s.ThermalHold && !deepCool) || (warm && !severe)) return Note("dipping, but it's warm: not adding power");
            if (warm && temp >= _trigger + CollapseHeatC) return Note($"FPS fell to {s.Fps:0} with the chip at {temp:0} °C: that's heat, not a power shortage, so no extra power");
            if (!binding)
            {
                var cap = Math.Min(_max, ceiling);
                if (!_simulated && Current < cap)
                {
                    // The reading says the limit isn't what's holding it back, yet frames are short. Readings can under-report,
                    // so test: add power and see whether FPS follows (the gain check undoes it if it doesn't).
                    if (_gain is not null || (now - _lastChange).TotalSeconds < 3) return null; // wait for the last test raise to be judged
                    var up = Math.Min(cap, Current + StepFor(s));
                    return Change(up, now, raised: true,
                        $"dipping (avg {s.Fps:0}, 1% low {s.LowFps:0}) though the chip reads only {s.DrawnWatts:0} of {Current} W; that reading can under-report, so testing +{up - Current} W",
                        fpsBefore: s.Fps, probe: true, lowBefore: s.LowFps);
                }
                // More power was tried (or isn't allowed) and FPS didn't follow: the game really isn't using the limit.
                // Trim it to just above what the game draws.
                if (peak60 is { } used && s.DrawnWatts is not null && (now - _lastChange).TotalSeconds >= 15)
                {
                    var want = Math.Max(_min, (int)Math.Ceiling(used + 4));
                    if (Current - want >= 4)
                    {
                        var trim = Change(want, now, raised: false, $"the game isn't using its power limit (peak {used:0} W of {Current} W), so a lower limit costs nothing");
                        _healthySince = null;
                        return trim;
                    }
                }
                return Note($"dipping but the chip draws only {s.DrawnWatts:0} of {Current} W and more power didn't help: CPU or engine limited");
            }
            if (Current >= Math.Min(_max, ceiling)) return Note(Current >= _max ? $"at the {_max} W limit and still dipping" : "holding here briefly: more power either overheated it or didn't help");
            if ((now - _lastChange).TotalSeconds < 3) return null;
            var next = Math.Min(Math.Min(_max, ceiling), Current + StepFor(s));
            var why = _simulated ? $"the game draws {s.DrawnWatts:0} W, the limit would have held it back" : $"dipping (avg {s.Fps:0}, 1% low {s.LowFps:0}) at the limit";
            return Change(next, now, raised: true, why, fpsBefore: s.Fps, lowBefore: s.LowFps);
        }

        // Room to spare: the usual bar, or clearly above the target (6 %+) with a 1% low that is still smooth: a 1% low a few points
        // under 94 % kept restarting the clock while the game ran 10 % over the target.
        var room = _simulated || hitchOnly || (s.LowFps >= _target * RoomLow && s.Fps >= _target * RoomAvg)
                   || (s.Fps >= _target * 1.06 && s.LowFps >= _target * SmoothFloor);
        if (!room) { _healthySince = null; return null; } // fine, but not by much: hold
        _healthySince ??= now;
        var held = (now - _healthySince.Value).TotalSeconds;
        if (_floor > _min && now >= _floorDecayAt) { _floor--; _floorDecayAt = now.AddSeconds(20); }
        var failed = _failed is { } f && now - f.at < TimeSpan.FromMinutes(10) ? f.w : (int?)null;
        var lowest = Math.Max(_min, Math.Max(_floor, failed is { } ff ? ff + 3 : _min));
        lowest = Math.Max(lowest, TypicalFloor(now)); // a quiet menu must not pull the limit far below what the game itself needs

        // 1. Snap to what the game actually draws (free: it doesn't use the headroom).
        if (!_drawUntrusted && peak60 is { } p && s.DrawnWatts is not null && held >= 15 && (now - _lastChange).TotalSeconds >= 15)
        {
            var want = (int)Math.Ceiling(p + 4);
            var next = Math.Max(want, lowest);
            if (Current - next >= 6 || (_simulated && Current - next >= 3))
                return Change(next, now, raised: false, $"the limit was well above what the game draws (peak {p:0} W in the last minute)");
        }
        // 2. Then creep down while the target is held with room to spare.
        // The further above the target, the bigger and quicker the steps down (3 W / 25 s at 10 %+ over, 2 W / 30 s at 5 %+, else 2 W / 40 s).
        var over = s.Fps / _target;
        var (step, wait) = over >= 1.10 ? (3, 25) : over >= 1.05 ? (2, 30) : (2, 40);
        if (held >= wait && (now - _lastChange).TotalSeconds >= wait)
        {
            var lower = Math.Max(lowest, Current - step);
            if (Current - lower >= 2) return Change(lower, now, raised: false, $"holding the target with room to spare ({over:P0} of it)");
        }
        return null;
    }

    /// <summary>
    /// How low the limit may be lowered to while healthy: 6 W under what the game really needed, which is last time's typical level
    /// and the level a real dip this session forced it up to (that one fades by 1 W a minute). Quiet time doesn't count as "need":
    /// without this a menu (low draw, easy FPS) dragged the limit to the floor, and leaving the menu stalled the game until power climbed back.
    /// </summary>
    private int TypicalFloor(DateTime now)
    {
        var need = _knownTypical ?? 0;
        if (_needLevel > 0) need = Math.Max(need, _needLevel - (int)(now - _needAt).TotalMinutes);
        return need > 0 ? Math.Max(_min, need - 6) : _min;
    }

    /// <summary>How much to add: more when the game is far under the target.</summary>
    private int StepFor(AutoTdpSample s)
    {
        if (_simulated) return 5;
        if (s.TempC is { } tc && tc >= _trigger - 5) return 5;   // warm: climb gently
        if (s.Fps < _target * 0.70) return 10;
        return s.LowFps < _target * 0.80 ? 8 : 5;
    }

    private AutoTdpDecision? Note(string text)
    {
        if (text == _lastNote) return null;
        _lastNote = text;
        return new AutoTdpDecision(null, text);
    }

    private AutoTdpDecision Change(int watts, DateTime now, bool raised, string why, double fpsBefore = 0, bool probe = false, double lowBefore = 0)
    {
        var prev = Current;
        Current = watts;
        MaxUsed = Math.Max(MaxUsed, watts);
        Changes++;
        _lastChange = now;
        _lastNote = null;
        if (raised)
        {
            _failed = (prev, now);
            _needLevel = watts; _needAt = now;
            _floor = watts; _floorDecayAt = now.AddSeconds(20);
            _healthySince = null;
            if (!_simulated) _gain = (now.AddSeconds(6), fpsBefore, lowBefore, prev, probe);
        }
        else _healthySince = now;
        return new AutoTdpDecision(watts, $"{(watts > prev ? "+" : "−")}{Math.Abs(watts - prev)} W → {watts} W: {why}");
    }
}

/// <summary>Why Auto TDP can't run for a game: a plain-English reason, so it never fails silently.</summary>
public static class AutoTdpGate
{
    /// <param name="requested">The mode chosen (per game, else the global one).</param>
    public static string? WhyNot(AutoTdpMode requested, bool isHandheldApu, bool safeMode, bool powerManagedForGame, bool ryzenAdjFound)
    {
        if (requested == AutoTdpMode.Off) return null;
        if (!isHandheldApu) return "this PC isn't a Ryzen APU handheld";
        if (safeMode) return "the launcher is in safe mode";
        if (!powerManagedForGame) return "power management is off for this game (game page → Power limit → tick Manage)";
        if (!ryzenAdjFound) return "RyzenAdj isn't set up (Settings → Power limit → Browse… to ryzenadj.exe)";
        return null;
    }
}
