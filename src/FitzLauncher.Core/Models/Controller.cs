namespace FitzLauncher.Core.Models;

public enum StickCurve { Linear, Smooth, Precise, Aggressive }

/// <summary>One analog stick's tuning. Percentages are of full deflection.</summary>
public sealed class StickSettings
{
    /// <summary>Ignore movement inside this radius (stops drift).</summary>
    public int InnerDeadzone { get; set; } = 8;
    /// <summary>Reach full output this far before the edge (makes the full range reachable on worn sticks).</summary>
    public int OuterDeadzone { get; set; } = 2;
    /// <summary>Start output at this level just outside the deadzone (counters a game's own deadzone).</summary>
    public int AntiDeadzone { get; set; }
    public StickCurve Curve { get; set; } = StickCurve.Linear;
    /// <summary>Output multiplier, 50–150%.</summary>
    public int Sensitivity { get; set; } = 100;
    public bool InvertY { get; set; }
}

/// <summary>Per-game controller tuning, applied through a virtual controller while the game runs.</summary>
public sealed class ControllerProfile
{
    public bool Enabled { get; set; }
    public StickSettings LeftStick { get; set; } = new();
    public StickSettings RightStick { get; set; } = new();
    /// <summary>Trigger presses below this (%) are ignored.</summary>
    public int TriggerDeadzone { get; set; } = 4;
    /// <summary>Full trigger output from this point (%): lower = hair trigger.</summary>
    public int TriggerMax { get; set; } = 100;
    /// <summary>Rumble strength, 0 (off) to 200%.</summary>
    public int Vibration { get; set; } = 100;
    /// <summary>Nintendo-style layout: A↔B and X↔Y.</summary>
    public bool SwapFaceButtons { get; set; }

    public ControllerProfile Clone() => new()
    {
        Enabled = Enabled, TriggerDeadzone = TriggerDeadzone, TriggerMax = TriggerMax, Vibration = Vibration, SwapFaceButtons = SwapFaceButtons,
        LeftStick = Copy(LeftStick), RightStick = Copy(RightStick),
    };

    private static StickSettings Copy(StickSettings s) => new()
    {
        InnerDeadzone = s.InnerDeadzone, OuterDeadzone = s.OuterDeadzone, AntiDeadzone = s.AntiDeadzone, Curve = s.Curve, Sensitivity = s.Sensitivity, InvertY = s.InvertY,
    };
}
