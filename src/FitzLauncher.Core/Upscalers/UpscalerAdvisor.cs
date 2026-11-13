using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Upscalers;

public enum GpuFamily { NvidiaRtx, NvidiaOther, AmdRdna4, AmdRdna3, AmdOlder, IntelArc, IntelIntegrated, Unknown }

/// <summary>The upscaling recommendation for one game on this PC.</summary>
public sealed class UpscalerAdvice
{
    public string InGame { get; set; } = "";
    public string Best { get; set; } = "";
    public string Why { get; set; } = "";
    public bool SuggestOptiScaler { get; set; }
    public string? OptiScalerWhy { get; set; }
    public string? FrameGeneration { get; set; }
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// Picks the best upscaler a game can use on this GPU: DLSS on RTX cards; on Radeon RDNA 4, FSR 4 (through FSR 3.1
/// games and AMD Software); on other Radeon and Intel GPUs, XeSS (usually the sharpest of the cross-vendor options)
/// or FSR 3.1. When the game only offers an upscaler your GPU can't run well (DLSS on AMD/Intel, old FSR 2),
/// it suggests OptiScaler, which feeds the game's upscaler input to XeSS or FSR 3.1 instead.
/// </summary>
public static partial class UpscalerAdvisor
{
    public static GpuFamily Family(GpuInfo? gpu)
    {
        if (gpu is null) return GpuFamily.Unknown;
        var n = gpu.Name;
        return gpu.Vendor switch
        {
            GpuVendor.Nvidia => n.Contains("RTX", StringComparison.OrdinalIgnoreCase) ? GpuFamily.NvidiaRtx : GpuFamily.NvidiaOther,
            GpuVendor.Amd when Rx9000().IsMatch(n) => GpuFamily.AmdRdna4,
            GpuVendor.Amd when gpu.IsRdna3OrNewer => GpuFamily.AmdRdna3,
            GpuVendor.Amd => GpuFamily.AmdOlder,
            GpuVendor.Intel when n.Contains("Arc", StringComparison.OrdinalIgnoreCase) => GpuFamily.IntelArc,
            GpuVendor.Intel => GpuFamily.IntelIntegrated,
            _ => GpuFamily.Unknown,
        };
    }

    [GeneratedRegex(@"RX\s?9\d{3}", RegexOptions.IgnoreCase)] private static partial Regex Rx9000();

    public static UpscalerAdvice Advise(GameScanResult scan, GpuInfo? gpu, GameModLayout? layout = null)
    {
        var a = new UpscalerAdvice();
        var family = Family(gpu);
        var has = scan.Upscalers.ToHashSet();
        var dlls = scan.UpscalerDlls.Select(d => d.Kind).ToHashSet();
        var fsr31 = dlls.Contains(UpscalerDllKind.FsrDx12) || dlls.Contains(UpscalerDllKind.FsrVulkan);
        var dlss = has.Contains(Upscaler.Dlss) || dlls.Contains(UpscalerDllKind.DlssSuperResolution);
        var xess = has.Contains(Upscaler.Xess) || dlls.Contains(UpscalerDllKind.XeSS);
        var fsr = has.Contains(Upscaler.Fsr) || fsr31;

        var list = new List<string>();
        if (dlss) list.Add("DLSS" + (dlls.Contains(UpscalerDllKind.DlssFrameGeneration) ? " + Frame Generation" : ""));
        if (fsr) list.Add(fsr31 ? "FSR 3.1+" : "FSR");
        if (xess) list.Add("XeSS");
        a.InGame = list.Count == 0 ? "No upscaler found in the game files." : "In this game: " + string.Join(", ", list) + ".";

        switch (family)
        {
            case GpuFamily.NvidiaRtx when dlss:
                (a.Best, a.Why) = ("DLSS", "DLSS gives the best image on RTX cards. Keep its DLL updated for the newest model.");
                break;
            case GpuFamily.AmdRdna4 when fsr31:
                (a.Best, a.Why) = ("FSR 4 (via FSR 3.1)", "This game uses the FSR 3.1 DLL, so AMD Software can upgrade it to FSR 4 on your RX 9000 card: pick FSR in the game and turn on the FSR 4 upgrade in AMD Software.");
                break;
            case GpuFamily.IntelArc when xess:
                (a.Best, a.Why) = ("XeSS", "XeSS runs on Arc's XMX units: the best image on Intel cards.");
                break;
            case GpuFamily.AmdRdna3 or GpuFamily.AmdRdna4 or GpuFamily.AmdOlder or GpuFamily.IntelIntegrated or GpuFamily.NvidiaOther when xess:
                (a.Best, a.Why) = ("XeSS", "On this GPU XeSS (DP4a) usually looks sharper and steadier than FSR 2/3 at the same quality preset, for a few % less FPS. Use FSR if you need the extra frames.");
                break;
            case GpuFamily.AmdRdna3 or GpuFamily.AmdRdna4 or GpuFamily.AmdOlder or GpuFamily.IntelIntegrated or GpuFamily.IntelArc or GpuFamily.NvidiaOther when fsr:
                (a.Best, a.Why) = (fsr31 ? "FSR 3.1" : "FSR", fsr31 ? "FSR 3.1 works on every GPU and its DLL can be kept up to date." : "The game's own FSR. It's an older version compiled into the game, so OptiScaler can give a newer upscaler.");
                break;
            default:
                (a.Best, a.Why) = (dlss && family is GpuFamily.NvidiaRtx ? "DLSS" : "Native or the game's own scaler",
                    list.Count == 0 ? "No modern upscaler in this game. Radeon Super Resolution (AMD features) can upscale it at driver level in fullscreen." : "None of this game's upscalers suit your GPU directly.");
                break;
        }

        // OptiScaler: when the game's upscaler isn't one this GPU runs (or is old FSR 2).
        var nonNvidia = family is not (GpuFamily.NvidiaRtx or GpuFamily.Unknown);
        if (nonNvidia && dlss && !xess && !fsr31)
        {
            a.SuggestOptiScaler = true;
            a.OptiScalerWhy = "This game only has DLSS, which can't run on your GPU. OptiScaler takes the game's DLSS input and runs XeSS or FSR 3.1 instead: pick DLSS in the game's settings, then choose the upscaler in OptiScaler's overlay (Insert key).";
        }
        else if (nonNvidia && fsr && !fsr31 && !xess)
        {
            a.SuggestOptiScaler = true;
            a.OptiScalerWhy = "The game's FSR is an old built-in version. OptiScaler can replace it with XeSS or FSR 3.1 in many games for a cleaner image.";
        }
        if (a.SuggestOptiScaler && layout?.HasAntiCheat == true)
        {
            a.SuggestOptiScaler = false;
            a.Notes.Add("OptiScaler isn't offered: this game has anti-cheat, which can ban for injected DLLs online.");
        }

        if (dlls.Contains(UpscalerDllKind.DlssFrameGeneration) && family != GpuFamily.NvidiaRtx)
            a.FrameGeneration = "DLSS Frame Generation needs an RTX 40/50 card. On your GPU use FSR 3 frame generation (OptiScaler can provide it) or AMD Fluid Motion Frames (AMD features).";
        else if (family is GpuFamily.AmdRdna3 or GpuFamily.AmdRdna4)
            a.FrameGeneration = "For more smoothness above 60 FPS, AMD Fluid Motion Frames works in any DirectX 11/12 game (AMD features on this page).";
        return a;
    }

    /// <summary>
    /// OptiScaler install plan: its main DLL goes next to the game exe under a proxy name the game will load
    /// (dxgi.dll, or winmm.dll / version.dll when dxgi.dll is taken by ReShade/DXVK), plus its other files.
    /// Returned as revertible file replacements.
    /// </summary>
    public static Tweak? OptiScalerTweak(string optiDir, string gameExeDir, Func<string, bool> existsInExeDir, GpuFamily family = GpuFamily.Unknown, UpscaleMode? mode = null)
    {
        var main = Directory.Exists(optiDir) ? Directory.EnumerateFiles(optiDir, "OptiScaler.dll", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (main is null) return null;
        var root = Path.GetDirectoryName(main)!;
        var proxy = new[] { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll" }.FirstOrDefault(n => !existsInExeDir(n)) ?? "winmm.dll";
        var actions = new List<TweakAction> { new FileReplaceAction { TargetPath = Path.Combine(gameExeDir, proxy), SourcePath = main, NewVersion = "OptiScaler" } };
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            var name = Path.GetFileName(f);
            if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Licenses", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(Path.Combine(gameExeDir, rel));
            if (!target.StartsWith(Path.GetFullPath(gameExeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // stays inside the game folder
            actions.Add(new FileReplaceAction { TargetPath = target, SourcePath = f, NewVersion = "OptiScaler" });
        }
        // Per-game settings go in after the copy, so Revert undoes them first (newest first) and then removes the file.
        if (actions.OfType<FileReplaceAction>().Any(a => Path.GetFileName(a.TargetPath).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)))
            foreach (var (section, key, value) in OptiScalerSettings.For(family, mode))
                actions.Add(new IniSetAction { FilePath = Path.Combine(gameExeDir, "OptiScaler.ini"), Section = section, Key = key, Value = value, CreateIfMissing = false });
        return new Tweak
        {
            Id = "optiscaler",
            Title = "OptiScaler (use XeSS or FSR 3.1 in this game)",
            Description = $"Installs OptiScaler as {proxy} next to the game. In the game pick DLSS (or its FSR/XeSS option), then press Insert in-game to choose the upscaler OptiScaler uses. Every file it adds or replaces is backed up; Revert removes it.",
            Category = TweakCategory.Graphics,
            VisualImpact = VisualImpact.None, // an image upgrade, not a cost
            Risk = TweakRisk.Moderate,
            OneClickEligible = false,
            ExpectedGain = "Better upscaler than the game offers your GPU",
            Actions = actions,
        };
    }
}
