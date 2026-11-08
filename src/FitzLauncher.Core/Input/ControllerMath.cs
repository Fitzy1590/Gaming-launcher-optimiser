using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Input;

/// <summary>
/// The transforms applied to a controller's state on its way to the game: radial stick deadzones (inner,
/// outer, anti-deadzone), response curves, sensitivity and inversion; trigger deadzone and hair trigger;
/// rumble scaling; optional A/B X/Y swap. XInput ranges: sticks -32768..32767, triggers 0..255, motors 0..65535.
/// </summary>
public static class ControllerMath
{
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public static (short x, short y) Stick(short rawX, short rawY, StickSettings s)
    {
        var x = rawX / 32767d;
        var y = rawY / 32767d;
        var mag = Math.Sqrt(x * x + y * y);
        if (mag < 1e-6) return (0, 0);

        var inner = Math.Clamp(s.InnerDeadzone, 0, 90) / 100d;
        var outer = 1 - Math.Clamp(s.OuterDeadzone, 0, 50) / 100d;
        if (mag <= inner) return (0, 0);

        // Rescale the live zone to 0..1, then curve, then lift by the anti-deadzone.
        var t = Math.Clamp((mag - inner) / Math.Max(outer - inner, 1e-3), 0, 1);
        t = s.Curve switch
        {
            StickCurve.Smooth => t * t * (3 - 2 * t),           // gentle near centre and edge
            StickCurve.Precise => t * t,                        // fine aiming near centre
            StickCurve.Aggressive => Math.Sqrt(t),              // fast response
            _ => t,
        };
        var anti = Math.Clamp(s.AntiDeadzone, 0, 50) / 100d;
        var outMag = Math.Clamp((anti + (1 - anti) * t) * Math.Clamp(s.Sensitivity, 50, 150) / 100d, 0, 1);

        var nx = x / mag * outMag;
        var ny = y / mag * outMag * (s.InvertY ? -1 : 1);
        return (ToShort(nx), ToShort(ny));
    }

    public static byte Trigger(byte raw, int deadzonePercent, int maxPercent)
    {
        var lo = Math.Clamp(deadzonePercent, 0, 90) / 100d * 255;
        var hi = Math.Clamp(maxPercent, Math.Clamp(deadzonePercent, 0, 90) + 5, 100) / 100d * 255;
        if (raw <= lo) return 0;
        return (byte)Math.Round(Math.Clamp((raw - lo) / (hi - lo), 0, 1) * 255);
    }

    /// <summary>Rumble from the game (0..255 per motor) scaled to the physical motor (0..65535).</summary>
    public static ushort Rumble(byte fromGame, int strengthPercent) =>
        (ushort)Math.Clamp(Math.Round(fromGame * 257d * Math.Clamp(strengthPercent, 0, 200) / 100d), 0, 65535);

    public static ushort Buttons(ushort raw, bool swapFace)
    {
        if (!swapFace) return raw;
        var b = (ushort)(raw & ~(A | B | X | Y));
        if ((raw & A) != 0) b |= B;
        if ((raw & B) != 0) b |= A;
        if ((raw & X) != 0) b |= Y;
        if ((raw & Y) != 0) b |= X;
        return b;
    }

    private static short ToShort(double v) => (short)Math.Clamp(Math.Round(v * 32767), -32768, 32767);
}
