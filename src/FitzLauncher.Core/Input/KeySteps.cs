using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Input;

/// <summary>One keyboard event to inject: a virtual key going down or up.</summary>
public readonly record struct KeyStep(byte Vk, bool Down);

/// <summary>Windows virtual-key codes used for injected shortcuts.</summary>
public static class Vk
{
    public const byte Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Tab = 0x09, Enter = 0x0D, Esc = 0x1B, Space = 0x20,
        PgUp = 0x21, PgDn = 0x22, End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Ins = 0x2D, Del = 0x2E,
        Num0 = 0x60, NumMultiply = 0x6A, NumAdd = 0x6B, NumSubtract = 0x6D, NumDecimal = 0x6E, NumDivide = 0x6F, F1 = 0x70;

    /// <summary>Keys that need the "extended" flag when injected (navigation cluster, numpad divide).</summary>
    public static bool IsExtended(byte vk) => vk is >= PgUp and <= Down or Ins or Del or NumDivide;
}

/// <summary>
/// Alt+Tab the way a keyboard does it: Alt goes down on the first press and stays down while more Tab presses cycle
/// through the windows; releasing Alt picks the highlighted one. Used while PS is held: each press of the shortcut
/// button is one Tab, letting go of PS lets go of Alt. Pure planning: the caller injects the steps.
/// </summary>
public sealed class AltTabHold
{
    public bool Held { get; private set; }

    /// <summary>Steps for one press: Alt down (first time only), then Tab (Shift+Tab to go back).</summary>
    public IReadOnlyList<KeyStep> Press(bool back = false)
    {
        var steps = new List<KeyStep>();
        if (!Held) { steps.Add(new(Vk.Alt, true)); Held = true; }
        if (back) steps.Add(new(Vk.Shift, true));
        steps.Add(new(Vk.Tab, true));
        steps.Add(new(Vk.Tab, false));
        if (back) steps.Add(new(Vk.Shift, false));
        return steps;
    }

    /// <summary>Steps when PS is let go: Alt up, which switches to the highlighted window.</summary>
    public IReadOnlyList<KeyStep> Release()
    {
        if (!Held) return [];
        Held = false;
        return [new(Vk.Alt, false)];
    }
}

/// <summary>A hotkey as a trainer lists it ("Ctrl+Num 1"): modifiers plus one key.</summary>
public sealed record HotkeyChord(IReadOnlyList<byte> Modifiers, byte Key)
{
    /// <summary>Modifiers down, key down and up, modifiers up (in reverse).</summary>
    public IReadOnlyList<KeyStep> Steps()
    {
        var steps = new List<KeyStep>();
        foreach (var m in Modifiers) steps.Add(new(m, true));
        steps.Add(new(Key, true));
        steps.Add(new(Key, false));
        foreach (var m in Modifiers.Reverse()) steps.Add(new(m, false));
        return steps;
    }
}

/// <summary>Reads trainer hotkey text ("Num 1", "Ctrl+Num 1", "Alt+Num /", "Ctrl+Shift+Home", "F5") into a <see cref="HotkeyChord"/>.</summary>
public static partial class HotkeyParser
{
    [GeneratedRegex(@"^\s*(?<mods>(?:(?:ctrl|control|alt|shift)\s*\+\s*)*)(?<key>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static HotkeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern().Match(text);
        if (!m.Success) return null;
        var mods = new List<byte>();
        foreach (var part in m.Groups["mods"].Value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var vk = part.ToLowerInvariant() switch { "ctrl" or "control" => Vk.Ctrl, "alt" => Vk.Alt, "shift" => Vk.Shift, _ => (byte)0 };
            if (vk != 0 && !mods.Contains(vk)) mods.Add(vk);
        }
        return KeyFor(m.Groups["key"].Value) is { } key ? new HotkeyChord(mods, key) : null;
    }

    private static byte? KeyFor(string raw)
    {
        var k = Regex.Replace(raw.ToLowerInvariant(), @"[\s_]", "");
        if (k.StartsWith("numpad")) k = "num" + k[6..];
        if (k.StartsWith("num") && k.Length == 4)
            return k[3] switch
            {
                >= '0' and <= '9' => (byte)(Vk.Num0 + (k[3] - '0')),
                '*' => Vk.NumMultiply, '+' => Vk.NumAdd, '-' => Vk.NumSubtract, '.' => Vk.NumDecimal, '/' => Vk.NumDivide,
                _ => null,
            };
        if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 12) return (byte)(Vk.F1 + f - 1);
        if (k.Length == 1 && (char.IsAsciiLetterLower(k[0]) || char.IsAsciiDigit(k[0]))) return (byte)char.ToUpperInvariant(k[0]);
        return k switch
        {
            "home" => Vk.Home, "end" => Vk.End, "ins" or "insert" => Vk.Ins, "del" or "delete" => Vk.Del,
            "pgup" or "pageup" => Vk.PgUp, "pgdn" or "pagedown" => Vk.PgDn,
            "up" => Vk.Up, "down" => Vk.Down, "left" => Vk.Left, "right" => Vk.Right,
            "space" => Vk.Space, "enter" => Vk.Enter, "tab" => Vk.Tab, "esc" or "escape" => Vk.Esc,
            _ => null,
        };
    }

    /// <summary>The hotkey FLiNG trainers reserve to switch all hotkeys off and on: never send it.</summary>
    public static bool IsTrainerMasterSwitch(HotkeyChord c) => c.Key == Vk.Home && c.Modifiers.Contains(Vk.Ctrl) && c.Modifiers.Contains(Vk.Shift);
}
