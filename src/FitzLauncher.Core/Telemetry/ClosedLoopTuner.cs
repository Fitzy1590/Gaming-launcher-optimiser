using System.Globalization;

namespace FitzLauncher.Core.Telemetry;

public enum TunerRule { CpuBound, GpuBound, MicroStutter, Thermal }

/// <summary>Apply and Advise start a rule; Release and Undo put it back; Note is information only.</summary>
public enum TunerActionType { Apply, Advise, Release, Undo, Note }

/// <summary>What the rule wants done. The applier (T4) maps each flag onto the existing class that owns that setting.</summary>
[Flags]
public enum TunerEffect
{
    None = 0,
    HighPriority = 1,
    PCoreAffinity = 2,
    HoldBackBackground = 4,
    RaisePowerProfile = 8,
    AdviseHags = 16,
    FrameCap = 32,
    TrimWorkingSets = 64,
    StockPowerProfile = 128,
    Toast = 256,
}

/// <summary>One decision. Release and Undo carry the effects that were applied, so the applier knows what to put back.</summary>
public sealed record TunerAction(TunerRule? Rule, TunerActionType Type, TunerEffect Effects, int? FrameCapFps, string Text)
{
    /// <summary>True when the effects change the game, the OS or the hardware (so they need a restore record). Advice and toasts do not.</summary>
    public bool ChangesSystem => (Effects & ~(TunerEffect.AdviseHags | TunerEffect.Toast)) != TunerEffect.None;
}

/// <summary>Things the tuner is told about the session, not measured from frames.</summary>
/// <param name="AutoTdpOn">Auto TDP owns the watts: Rule 2 becomes advice and Rule 4 defers to the thermal guard.</param>
/// <param name="ThermalGuardActing">The thermal guard is cooling things down right now.</param>
/// <param name="AntiCheat">No priority, affinity or memory actions for this game.</param>
/// <param name="HasPowerProfile">The machine has a power profile that Rule 2 can raise.</param>
/// <param name="MonitorHz">Refresh rate of the screen the game is on (0 when unknown).</param>
public readonly record struct TunerContext(bool AutoTdpOn, bool ThermalGuardActing, bool AntiCheat, bool HasPowerProfile, int MonitorHz);

/// <summary>A rule's state: on now, given up for the session, how many times it acted, and how long until it may act again.</summary>
public readonly record struct TunerRuleStatus(TunerRule Rule, bool Active, bool GivenUp, int Applications, TimeSpan CooldownLeft);

/// <summary>One second of input: the latest frame bucket, the temperatures, and the context. Temperatures are null when no sensor can tell.</summary>
public readonly record struct TunerSample(FrameBucket Bucket, double? CpuTempC, double? GpuTempC, TunerContext Context);

/// <summary>
/// The four closed-loop rules (SPEC §12.2, <c>.claude/rules/telemetry.md</c>). Pure state machine, tested with a fake clock.
/// <list type="bullet">
/// <item>Call <see cref="Update"/> once per second with the newest one-second bucket, from one sampler thread under a lock.</item>
/// <item>A rule fires only after 3 consecutive buckets. Cooldown runs from the last change (apply or release).</item>
/// <item>Thermal beats the rest: while it is active, or the machine is over the limit, nothing else moves. Max 3 applications per rule per session.</item>
/// <item>A change that made 1% low (or temperature) worse over the next 16 s is undone and not retried.</item>
/// <item>No busy metrics means Rules 1 and 2 do nothing and say so once. Missing temperatures mean Rule 4 does nothing. It never guesses.
/// If an active rule's input vanishes, Rules 1 to 3 are put back after their normal release time; Rule 4 stays until it reads cool or the game ends.</item>
/// </list>
/// </summary>
public sealed class ClosedLoopTuner
{
    public const int HoldBuckets = 3;
    public const int MaxApplicationsPerRule = 3;
    public const double BoundRatio = 1.15, BoundReleaseRatio = 1.05;
    public const double StutterTrigger = 0.70, StutterRelease = 0.80;
    public const double HotC = 85, CoolC = 80;
    /// <summary>A change is "worse" when 1% low falls below this share of where it was, or the temperature rises by <see cref="WorseTempC"/>.</summary>
    public const double WorseFraction = 0.95, WorseTempC = 2;
    public const int FrameCapBelowHz = 3, MinFrameCapFps = 20;
    public static readonly TimeSpan JudgeAfter = TimeSpan.FromSeconds(16);
    public static readonly TimeSpan BoundCooldown = TimeSpan.FromSeconds(60), BoundReleaseAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StutterCooldown = TimeSpan.FromSeconds(30), StutterReleaseAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ThermalCooldown = TimeSpan.FromSeconds(20), ThermalReleaseAfter = TimeSpan.FromSeconds(30);

    private sealed class RuleState(TimeSpan cooldown, TimeSpan releaseAfter)
    {
        public readonly TimeSpan Cooldown = cooldown, ReleaseAfter = releaseAfter;
        public int Hold, Applications;
        public bool Active, Disabled;
        public TunerEffect Applied;
        public DateTime CooldownUntil = DateTime.MinValue;
        public DateTime? ClearSince;
        public (DateTime at, double baseline)? Judge;
    }

    private readonly RuleState[] _rules =
    [
        new(BoundCooldown, BoundReleaseAfter), new(BoundCooldown, BoundReleaseAfter),
        new(StutterCooldown, StutterReleaseAfter), new(ThermalCooldown, ThermalReleaseAfter),
    ];
    private readonly Queue<double> _lows = new(), _temps = new();
    private bool _busyNoted;

    private RuleState State(TunerRule r) => _rules[(int)r];

    /// <summary>Where each rule stands, for the Telemetry page.</summary>
    public IReadOnlyList<TunerRuleStatus> Status(DateTime now) =>
        [.. _rules.Select((r, i) => new TunerRuleStatus((TunerRule)i, r.Active, r.Disabled, r.Applications,
            r.Active || now >= r.CooldownUntil ? TimeSpan.Zero : r.CooldownUntil - now))];

    public IReadOnlyList<TunerAction> Update(in TunerSample s, DateTime now)
    {
        var output = new List<TunerAction>();
        var b = s.Bucket;
        var ctx = s.Context;
        var hasFrames = b.Frames > 0;
        if (hasFrames) Push(_lows, b.OnePercentLowFps);
        var temp = HotterOf(s.CpuTempC, s.GpuTempC);
        if (temp is { } t) Push(_temps, t);

        for (var i = 0; i < _rules.Length; i++) Judge((TunerRule)i, now, output);

        // Rule 4 first: it beats every other rule.
        var thermal = State(TunerRule.Thermal);
        Step(TunerRule.Thermal, temp is > HotC, temp is < CoolC, now, output, () => ThermalAction(ctx, temp));
        // Too hot also holds the others, even when Rule 4 itself can't act (cooldown, or out of tries).
        if (thermal.Active || temp is > HotC)
        {
            foreach (var r in new[] { TunerRule.CpuBound, TunerRule.GpuBound, TunerRule.MicroStutter }) State(r).Hold = 0;
            return output;
        }

        // Rules 1 and 2: need both busy numbers. If they vanish while a rule is on, that counts as "clear" so it is put back after the normal release time.
        var cpu = b.MedianCpuBusyMs;
        var gpu = b.MedianGpuBusyMs;
        double? ratio = hasFrames && cpu is > 0 && gpu is > 0 ? cpu / gpu : null;
        if (hasFrames && ratio is null)
        {
            if (!_busyNoted)
            {
                _busyNoted = true;
                output.Add(new TunerAction(null, TunerActionType.Note, TunerEffect.None, null,
                    "Can't tell whether the processor or the graphics chip is the limit (no busy readings), so the CPU-bound and GPU-bound rules are off."));
            }
        }
        else if (ratio is not null) _busyNoted = false;

        var cpuOverGpu = ratio ?? 0;
        var gpuOverCpu = ratio is > 0 ? 1 / ratio.Value : 0;
        Step(TunerRule.CpuBound, ratio is not null && cpuOverGpu > BoundRatio, ratio is null || cpuOverGpu < BoundReleaseRatio, now, output,
            () => CpuAction(ctx, cpuOverGpu));
        Step(TunerRule.GpuBound, ratio is not null && gpuOverCpu > BoundRatio, ratio is null || gpuOverCpu < BoundReleaseRatio, now, output,
            () => GpuAction(ctx, gpuOverCpu));

        // Rule 3: 1% low against the average.
        var avg = b.AvgFps;
        var low = b.OnePercentLowFps;
        Step(TunerRule.MicroStutter, hasFrames && avg > 0 && low < StutterTrigger * avg, !hasFrames || avg <= 0 || low >= StutterRelease * avg, now, output,
            () => StutterAction(ctx, low, avg));
        return output;
    }

    /// <summary>The game ended: everything that is active is released. Safe to call more than once.</summary>
    public IReadOnlyList<TunerAction> EndSession()
    {
        var output = new List<TunerAction>();
        for (var i = 0; i < _rules.Length; i++)
        {
            var r = _rules[i];
            if (r.Active) output.Add(new TunerAction((TunerRule)i, TunerActionType.Release, r.Applied, null, "The game ended, so everything was put back."));
            _rules[i] = new RuleState(r.Cooldown, r.ReleaseAfter);
        }
        _lows.Clear();
        _temps.Clear();
        _busyNoted = false;
        return output;
    }

    private void Step(TunerRule rule, bool trigger, bool clear, DateTime now, List<TunerAction> output, Func<TunerAction> make)
    {
        var r = State(rule);
        if (r.Active)
        {
            if (!clear) { r.ClearSince = null; return; }
            r.ClearSince ??= now;
            if (now - r.ClearSince.Value < r.ReleaseAfter) return;
            output.Add(new TunerAction(rule, TunerActionType.Release, r.Applied, null, "Conditions are back to normal, so it was put back."));
            r.Active = false;
            r.Applied = TunerEffect.None;
            r.Judge = null;
            r.ClearSince = null;
            r.CooldownUntil = now + r.Cooldown;
            return;
        }

        if (r.Disabled || now < r.CooldownUntil) { r.Hold = 0; return; }
        r.Hold = trigger ? r.Hold + 1 : 0;
        if (r.Hold < HoldBuckets) return;
        r.Hold = 0;

        var action = make();
        if (action.ChangesSystem && r.Applications >= MaxApplicationsPerRule)
        {
            r.Disabled = true;
            output.Add(new TunerAction(rule, TunerActionType.Note, TunerEffect.None, null,
                $"This rule has already acted {MaxApplicationsPerRule} times this session, so it has stopped trying."));
            return;
        }

        r.Active = true;
        r.Applied = action.Effects;
        r.ClearSince = null;
        r.CooldownUntil = now + r.Cooldown;
        if (action.ChangesSystem)
        {
            r.Applications++;
            if (Mean(rule == TunerRule.Thermal ? _temps : _lows) is { } baseline) r.Judge = (now, baseline);
        }
        output.Add(action);
    }

    /// <summary>16 s after a change: if it made things worse, undo it and never retry this session.</summary>
    private void Judge(TunerRule rule, DateTime now, List<TunerAction> output)
    {
        var r = State(rule);
        if (r.Judge is not { } j || !r.Active || now - j.at < JudgeAfter) return;
        r.Judge = null;
        var thermal = rule == TunerRule.Thermal;
        if (Mean(thermal ? _temps : _lows) is not { } current) return;
        var worse = thermal ? current > j.baseline + WorseTempC : current < j.baseline * WorseFraction;
        // Never take cooling away while it is still too hot: a power profile that has not yet helped is better than none.
        if (!worse || (thermal && current > HotC)) return;

        output.Add(new TunerAction(rule, TunerActionType.Undo, r.Applied, null,
            thermal ? "The change didn't cool things down, so it was undone and won't be tried again this session."
                    : "The change made the 1% lows worse, so it was undone and won't be tried again this session."));
        r.Active = false;
        r.Disabled = true;
        r.Applied = TunerEffect.None;
        r.ClearSince = null;
    }

    private static TunerAction CpuAction(TunerContext ctx, double ratio)
    {
        var why = $"The processor is the limit (it is busy {ratio.ToString("0.0", CultureInfo.InvariantCulture)}× as long as the graphics chip).";
        if (ctx.AntiCheat)
            return new TunerAction(TunerRule.CpuBound, TunerActionType.Advise, TunerEffect.None, null, why + " This game uses anti-cheat, so priority and CPU settings are left alone.");
        return new TunerAction(TunerRule.CpuBound, TunerActionType.Apply,
            TunerEffect.HighPriority | TunerEffect.PCoreAffinity | TunerEffect.HoldBackBackground, null,
            why + " Giving the game higher priority, the fast cores, and holding back background apps.");
    }

    private static TunerAction GpuAction(TunerContext ctx, double ratio)
    {
        var why = $"The graphics chip is the limit (it is busy {ratio.ToString("0.0", CultureInfo.InvariantCulture)}× as long as the processor).";
        var effects = TunerEffect.AdviseHags;
        if (ctx.HasPowerProfile && !ctx.AutoTdpOn) effects |= TunerEffect.RaisePowerProfile;
        var text = effects.HasFlag(TunerEffect.RaisePowerProfile)
            ? why + " Raising the power profile. Hardware GPU scheduling is worth checking."
            : ctx.AutoTdpOn ? why + " Auto TDP controls power, so this is advice only. Hardware GPU scheduling is worth checking."
                            : why + " This machine has no power profile to raise. Hardware GPU scheduling is worth checking.";
        return new TunerAction(TunerRule.GpuBound, effects.HasFlag(TunerEffect.RaisePowerProfile) ? TunerActionType.Apply : TunerActionType.Advise, effects, null, text);
    }

    private static TunerAction StutterAction(TunerContext ctx, double low, double avg)
    {
        var why = $"Stuttering: the slowest 1% of frames run at {low.ToString("0", CultureInfo.InvariantCulture)} FPS against an average of {avg.ToString("0", CultureInfo.InvariantCulture)}.";
        var effects = TunerEffect.None;
        int? cap = null;
        if (ctx.MonitorHz > 0)
        {
            effects |= TunerEffect.FrameCap;
            cap = Math.Max(MinFrameCapFps, ctx.MonitorHz - FrameCapBelowHz);
        }
        if (!ctx.AntiCheat) effects |= TunerEffect.TrimWorkingSets;
        var text = why + (cap is { } c ? $" Capping the frame rate at {c} FPS" : " No screen refresh rate known, so no frame cap")
                       + (ctx.AntiCheat ? "; memory is left alone because of anti-cheat." : " and trimming spare memory.");
        return new TunerAction(TunerRule.MicroStutter, effects == TunerEffect.None ? TunerActionType.Advise : TunerActionType.Apply, effects, cap, text);
    }

    private static TunerAction ThermalAction(TunerContext ctx, double? temp)
    {
        var why = $"Too hot ({(temp ?? 0).ToString("0", CultureInfo.InvariantCulture)} °C).";
        if (ctx.AutoTdpOn || ctx.ThermalGuardActing)
            return new TunerAction(TunerRule.Thermal, TunerActionType.Advise, TunerEffect.Toast, null, why + " The thermal guard is already looking after power, so only a warning is shown.");
        return new TunerAction(TunerRule.Thermal, TunerActionType.Apply, TunerEffect.StockPowerProfile | TunerEffect.Toast, null,
            why + " Going back to the standard power profile to cool down.");
    }

    private static double? HotterOf(double? a, double? b) => a is { } x ? (b is { } y ? Math.Max(x, y) : x) : b;

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HoldBuckets) q.Dequeue();
    }

    private static double? Mean(Queue<double> q) => q.Count == 0 ? null : q.Average();
}
