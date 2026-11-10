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
    private const double SmoothFloor = 0.90, AvgFloor = 0.95, RoomLow = 0.94, RoomAvg = 0.98;

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
    private (DateTime at, double fps, int prev, bool probe)? _gain;
    /// <summary>A raise helped while the power reading said the limit wasn't binding: the reading under-reports, so it is no longer used to hold power back or to trim.</summary>
    private bool _drawUntrusted;
    private int _floor, _ceiling;
    private string? _lastNote;
    private double _seconds, _limitSeconds, _drawnSeconds, _drawnTotal, _healthySeconds;

    /// <param name="thermalTriggerC">The temperature (the thermal guard's trigger) above which it lowers power to cool the chip.</param>
    public AutoTdpGovernor(int target, int minWatts, int maxWatts, int startWatts, bool simulated = false, double thermalTriggerC = 90)
    {
        (_target, _min, _max, _simulated, _trigger) = (target, minWatts, Math.Max(maxWatts, minWatts), simulated, thermalTriggerC);
        Current = Math.Clamp(startWatts, _min, _max);
        _floor = _min;
        _ceiling = _max;
        MaxUsed = Current;
    }

    public int Current { get; private set; }

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
        var distress = _simulated ? s.DrawnWatts is { } sd && sd >= Current - 1.5 : s.LowFps < _target * SmoothFloor || s.Fps < _target * AvgFloor;
        if (!distress) { _healthySeconds += dt; _levelSeconds[Current] = _levelSeconds.GetValueOrDefault(Current) + dt; }

        // Too hot: the fix that costs the least is less power (the frame cap stays put). Stop once it's below the trigger.
        var temp = s.TempC ?? 0;
        var hot = temp > 0 && temp >= _trigger;
        var warm = temp > 0 && temp >= _trigger - 5;
        if (hot)
        {
            _hotEpisode = true;
            _healthySince = null;
            if (Current > _min)
            {
                if ((now - _lastChange).TotalSeconds < 12) return null; // let the last cut take effect
                var next = Math.Max(_min, Current - (temp >= _trigger + 4 ? 5 : 3));
                var cut = Change(next, now, raised: false, $"{temp:0} °C: lowering power to cool it, keeping the FPS target");
                _healthySince = null;
                return cut;
            }
            return Note($"{temp:0} °C at the {_min} W floor: the thermal guard eases the FPS cap instead");
        }
        if (_hotEpisode && temp > 0 && temp < _trigger - 3)
        {
            // Cooled down: don't climb straight back to the power that heated it.
            _hotEpisode = false;
            _ceiling = Math.Min(_max, Current + 4);
            _ceilingUntil = now.AddMinutes(5);
        }

        // Did the last raise actually help?
        if (_gain is { } g && now >= g.at)
        {
            _gain = null;
            if (distress && s.Fps < g.fps * 1.02)
            {
                _ceiling = g.prev; _ceilingUntil = now.AddMinutes(g.probe ? 10 : 5);
                return Change(g.prev, now, raised: false, $"extra watts didn't improve FPS ({s.Fps:0}); back to {g.prev} W and not asking for more for a while");
            }
            if (g.probe && s.Fps >= g.fps * 1.05) _drawUntrusted = true;
        }

        var ceiling = now < _ceilingUntil ? _ceiling : _max;
        if (distress)
        {
            _healthySince = null;
            if (warm || s.ThermalHold) return Note("dipping, but it's warm: not adding power");
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
                        fpsBefore: s.Fps, probe: true);
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
            if (Current >= Math.Min(_max, ceiling)) return Note(Current >= _max ? $"at the {_max} W limit and still dipping" : "holding here for a few minutes: more power either overheated it or didn't help");
            if ((now - _lastChange).TotalSeconds < 3) return null;
            var next = Math.Min(Math.Min(_max, ceiling), Current + StepFor(s));
            var why = _simulated ? $"the game draws {s.DrawnWatts:0} W, the limit would have held it back" : $"dipping (avg {s.Fps:0}, 1% low {s.LowFps:0}) at the limit";
            return Change(next, now, raised: true, why, fpsBefore: s.Fps);
        }

        var room = _simulated || (s.LowFps >= _target * RoomLow && s.Fps >= _target * RoomAvg);
        if (!room) { _healthySince = null; return null; } // fine, but not by much: hold
        _healthySince ??= now;
        var held = (now - _healthySince.Value).TotalSeconds;
        if (_floor > _min && now >= _floorDecayAt) { _floor--; _floorDecayAt = now.AddSeconds(20); }
        var failed = _failed is { } f && now - f.at < TimeSpan.FromMinutes(10) ? f.w : (int?)null;
        var lowest = Math.Max(_min, Math.Max(_floor, failed is { } ff ? ff + 3 : _min));

        // 1. Snap to what the game actually draws (free: it doesn't use the headroom).
        if (!_drawUntrusted && peak60 is { } p && s.DrawnWatts is not null && held >= 15 && (now - _lastChange).TotalSeconds >= 15)
        {
            var want = (int)Math.Ceiling(p + 4);
            var next = Math.Max(want, lowest);
            if (Current - next >= 6 || (_simulated && Current - next >= 3))
                return Change(next, now, raised: false, $"the limit was well above what the game draws (peak {p:0} W in the last minute)");
        }
        // 2. Then creep down while the target is held with room to spare.
        if (held >= 40 && (now - _lastChange).TotalSeconds >= 40 && Current - 2 >= lowest)
            return Change(Current - 2, now, raised: false, "holding the target with room to spare");
        return null;
    }

    /// <summary>How much to add: more when the game is far under the target.</summary>
    private int StepFor(AutoTdpSample s)
    {
        if (_simulated) return 5;
        if (s.Fps < _target * 0.70) return 10;
        return s.LowFps < _target * 0.80 ? 8 : 5;
    }

    private AutoTdpDecision? Note(string text)
    {
        if (text == _lastNote) return null;
        _lastNote = text;
        return new AutoTdpDecision(null, text);
    }

    private AutoTdpDecision Change(int watts, DateTime now, bool raised, string why, double fpsBefore = 0, bool probe = false)
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
            _floor = watts; _floorDecayAt = now.AddSeconds(20);
            _healthySince = null;
            if (!_simulated) _gain = (now.AddSeconds(6), fpsBefore, prev, probe);
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
