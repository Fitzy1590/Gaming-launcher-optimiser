using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.Core.Upscalers;

/// <summary>
/// DXVK (DirectX 9/10/11 → Vulkan) for older games that stutter or run poorly on AMD/Intel drivers.
/// Copies the matching 32/64-bit DLLs next to the game exe. Each copy is a normal revertible file swap,
/// and any file it replaces is backed up. Skipped when ReShade/ENB or another wrapper already uses those DLL names.
/// </summary>
public static class DxvkPlanner
{
    public const string TweakId = "dxvk";

    public static Tweak? Plan(Game game, GameScanResult scan, string dxvkRoot, HardwareProfile? hw)
    {
        var exe = scan.RenderExePath ?? game.ExePath;
        var imports = PeImports.Read(exe);
        var api = imports.Contains("d3d9.dll") ? "DirectX 9" : imports.Contains("d3d11.dll") || imports.Contains("dxgi.dll") ? "DirectX 11" : null;
        if (api is null || scan.UsesDx12 || scan.UsesVulkan) return null;
        if (scan.Injectors.Count > 0) return null; // ReShade/ENB/loader already owns these names

        var arch = PeImports.Is64Bit(exe) ? "x64" : "x32";
        var dir = Path.GetDirectoryName(exe)!;
        string[] dlls = api == "DirectX 9" ? ["d3d9.dll"] : ["d3d11.dll", "dxgi.dll", "d3d10core.dll"];
        var actions = dlls.Select(d => (TweakAction)new FileReplaceAction
        {
            TargetPath = Path.Combine(dir, d), SourcePath = Path.Combine(dxvkRoot, arch, d), NewVersion = "dxvk",
        }).ToList();

        var amdOrIntel = hw?.PrimaryGpu?.Vendor is GpuVendor.Amd or GpuVendor.Intel;
        return new Tweak
        {
            Id = TweakId,
            Title = $"Run through DXVK ({api} → Vulkan)",
            Description = $"This is a {api} game. DXVK translates it to Vulkan, which on {(amdOrIntel ? "Radeon and Intel graphics" : "many GPUs")} often removes stutter and raises FPS in older titles. " +
                          "The first minutes may hitch while Vulkan shaders build. Revert if a game misbehaves; avoid it in online games with anti-cheat.",
            Category = TweakCategory.Stutter,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Moderate,
            OneClickEligible = false,
            ExpectedGain = api == "DirectX 9" ? "Often big for DX9 games" : "Smoother in some DX11 games",
            Recommended = amdOrIntel && api == "DirectX 9",
            Actions = actions,
        };
    }
}
