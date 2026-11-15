using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Upscalers;

public enum AmdNrLevel { NotAvailable, Experimental, Supported }

/// <summary>Whether AMD NR can be offered for this PC, with a plain-English reason (SPEC §13, FEATURE_PROGRESS §7b).</summary>
public sealed record AmdNrEligibility(AmdNrLevel Level, string Reason)
{
    public bool CanOffer => Level != AmdNrLevel.NotAvailable;
}

/// <summary>
/// GPU gate for the AMD NR build of OptiScaler. Chip lists are from its README ("Requirements"):
/// RX 9000 and RX 7000 supported; Strix Halo and 12+ CU handheld APUs experimental (lmxxf runtime only);
/// Z1 (4 CU), 760M/740M, 860M/840M and RX 6000 (needs AMD's HIP SDK) are not offered.
/// The driver (Adrenalin 26.9.1 or newer) is not checked: Windows reports a different driver version string.
/// </summary>
public static partial class AmdNrSupport
{
    public const string DriverNote = "Needs AMD Adrenalin 26.9.1 or newer.";

    [GeneratedRegex(@"(?<!\d)(7[46]0M|8[46]0M)\b|Z1(?!\s*Extreme)\b|Steam\s?Deck|Van\s?Gogh", RegexOptions.IgnoreCase)] private static partial Regex TooSmall();
    [GeneratedRegex(@"(?<!\d)(780M|880M|890M|8060S|8050S)\b|AI\s?MAX", RegexOptions.IgnoreCase)] private static partial Regex Handheld();
    [GeneratedRegex(@"RX\s?[79]\d{3}", RegexOptions.IgnoreCase)] private static partial Regex Rx7000Or9000();
    [GeneratedRegex(@"RX\s?6\d{3}", RegexOptions.IgnoreCase)] private static partial Regex Rx6000();

    public static AmdNrEligibility Check(GpuInfo? gpu, bool hasAntiCheat)
    {
        if (hasAntiCheat)
            return new(AmdNrLevel.NotAvailable, "This game has anti-cheat, which can ban for replaced DLLs, so AMD NR isn't offered.");
        if (gpu is not { Vendor: GpuVendor.Amd })
            return new(AmdNrLevel.NotAvailable, "AMD NR only works on AMD graphics.");

        var n = gpu.Name;
        if (TooSmall().IsMatch(n))
            return new(AmdNrLevel.NotAvailable, "This AMD chip is too small for AMD NR.");
        if (Rx6000().IsMatch(n))
            return new(AmdNrLevel.NotAvailable, "RX 6000 cards need AMD's HIP SDK installed, which isn't supported here yet.");
        if (Rx7000Or9000().IsMatch(n))
            return new(AmdNrLevel.Supported, DriverNote);
        if (Handheld().IsMatch(n))
            return new(AmdNrLevel.Experimental, "Experimental and slow on this chip. " + DriverNote);
        return new(AmdNrLevel.NotAvailable, "AMD NR supports Radeon RX 7000 and 9000 cards and some newer handheld chips.");
    }
}
