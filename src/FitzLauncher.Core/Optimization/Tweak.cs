using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

public enum VisualImpact { None, Low, Medium, High }
public enum TweakRisk { Safe, Moderate, Experimental }
public enum TweakCategory { FrameCap, Stutter, Latency, Graphics, System, Hardware, Info }

/// <summary>One optimisation the launcher proposes, made of one or more concrete actions.</summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public TweakCategory Category { get; init; }
    public VisualImpact VisualImpact { get; init; }
    public TweakRisk Risk { get; init; }
    /// <summary>Plain-English expected effect, e.g. "Fewer traversal hitches".</summary>
    public string ExpectedGain { get; init; } = "";
    public List<TweakAction> Actions { get; init; } = [];
    /// <summary>Why it is flagged as recommended for this machine/game right now.</summary>
    public string? Reason { get; set; }
    public bool Recommended { get; set; }
    /// <summary>Needs a reboot / game restart to take effect.</summary>
    public bool NeedsRestart { get; init; }

    /// <summary>True when the launcher can apply it itself (vs. a manual checklist item).</summary>
    public bool IsAutomatic => Actions.Count > 0 && Actions.All(a => a is not ManualStepAction);

    /// <summary>The 1-click "Optimise" button applies only tweaks that never reduce image quality.</summary>
    public bool PartOfOneClick => IsAutomatic && VisualImpact == VisualImpact.None && Risk != TweakRisk.Experimental;
}

public sealed class OptimizationPlan
{
    public List<Tweak> Tweaks { get; } = [];
    public List<string> Notes { get; } = [];
    /// <summary>The FPS target the plan was built for (null = uncapped).</summary>
    public int? Target { get; init; }
    public int? SuggestedTarget { get; set; }
    public string? SuggestedTargetReason { get; set; }

    public IEnumerable<Tweak> OneClick => Tweaks.Where(t => t.PartOfOneClick);
    public IEnumerable<Tweak> Suggestions => Tweaks.Where(t => !t.PartOfOneClick);
}
