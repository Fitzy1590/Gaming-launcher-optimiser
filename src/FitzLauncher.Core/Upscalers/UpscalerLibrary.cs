using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Upscalers;

/// <summary>
/// DLSS / XeSS / FSR 3.1 DLL updater. The newest copy of each DLL is taken from your other games
/// (and an optional folder of DLLs you downloaded), then offered as a backed-up, revertible swap.
/// Nothing is downloaded. FSR 2.x is compiled into games and can't be swapped, so it isn't offered.
/// </summary>
public static class UpscalerLibrary
{
    public static string FileName(UpscalerDllKind k) => k switch
    {
        UpscalerDllKind.DlssSuperResolution => "nvngx_dlss.dll",
        UpscalerDllKind.DlssFrameGeneration => "nvngx_dlssg.dll",
        UpscalerDllKind.DlssRayReconstruction => "nvngx_dlssd.dll",
        UpscalerDllKind.XeSS => "libxess.dll",
        UpscalerDllKind.FsrDx12 => "amd_fidelityfx_dx12.dll",
        _ => "amd_fidelityfx_vk.dll",
    };

    public static string Label(UpscalerDllKind k) => k switch
    {
        UpscalerDllKind.DlssSuperResolution => "DLSS Super Resolution",
        UpscalerDllKind.DlssFrameGeneration => "DLSS Frame Generation",
        UpscalerDllKind.DlssRayReconstruction => "DLSS Ray Reconstruction",
        UpscalerDllKind.XeSS => "Intel XeSS",
        UpscalerDllKind.FsrDx12 => "AMD FSR 3.1+ (DX12)",
        _ => "AMD FSR 3.1+ (Vulkan)",
    };

    /// <summary>Newest known copy of each DLL kind across all sources.</summary>
    public static Dictionary<UpscalerDllKind, UpscalerDllInfo> BestVersions(IEnumerable<UpscalerDllInfo> all) =>
        all.Where(d => File.Exists(d.Path) && TryVersion(d.Version, out _))
           .GroupBy(d => d.Kind)
           .ToDictionary(g => g.Key, g => g.OrderByDescending(d => { TryVersion(d.Version, out var v); return v; }).First());

    /// <summary>Upgrade tweaks for one game: every DLL that has a strictly newer copy available.</summary>
    public static List<Tweak> Upgrades(GameScanResult scan, IReadOnlyDictionary<UpscalerDllKind, UpscalerDllInfo> best)
    {
        var list = new List<Tweak>();
        foreach (var dll in scan.UpscalerDlls)
        {
            if (!best.TryGetValue(dll.Kind, out var newest)) continue;
            if (!TryVersion(newest.Version, out var nv)) continue;
            if (TryVersion(dll.Version, out var cv) && nv <= cv) continue;
            if (newest.Path.Equals(dll.Path, StringComparison.OrdinalIgnoreCase)) continue;
            var label = Label(dll.Kind);
            list.Add(new Tweak
            {
                Id = $"dll-{dll.Kind}-{Path.GetFileName(Path.GetDirectoryName(dll.Path))}".ToLowerInvariant(),
                Title = $"Update {label}: {Short(dll.Version)} → {Short(newest.Version)}",
                Description = $"Swaps in the newer {FileName(dll.Kind)} (taken from {SourceName(newest.Path)}). Newer versions usually give a sharper, more stable image at the same FPS. " +
                              "The original is backed up and can be restored. Avoid this in online games with anti-cheat, which can flag modified files.",
                Category = TweakCategory.Graphics,
                VisualImpact = VisualImpact.None,
                Risk = TweakRisk.Moderate,
                OneClickEligible = false,
                ExpectedGain = "Better image quality, same FPS",
                Actions = [new FileReplaceAction { TargetPath = dll.Path, SourcePath = newest.Path, NewVersion = newest.Version }],
            });
        }
        return list;
    }

    private static string SourceName(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        return dir.Parent is { } p ? $"{p.Name}\\{dir.Name}" : dir.Name;
    }

    private static string Short(string v) => string.IsNullOrEmpty(v) ? "unknown" : v;

    public static bool TryVersion(string? s, out Version v)
    {
        v = new Version();
        return !string.IsNullOrWhiteSpace(s) && Version.TryParse(s.Trim(), out v!);
    }
}
