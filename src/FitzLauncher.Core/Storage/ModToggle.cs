namespace FitzLauncher.Core.Storage;

public sealed record ModDll(string Path, string Kind, bool Enabled);

/// <summary>
/// Turns ReShade / ENB on or off for a game by renaming their wrapper DLL (dxgi.dll, d3d11.dll…) to
/// "*.fitz-off" and back. Nothing is deleted, and the on/off state is simply which name the file has.
/// A DLL only counts as a mod when its description says ReShade/ENB, or when its ini/shader folder sits
/// next to it. That way DXVK, Special K or a crack's own dxgi.dll is never touched.
/// </summary>
public static class ModToggle
{
    public const string OffSuffix = ".fitz-off";
    private static readonly string[] WrapperNames = ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "d3d8.dll", "opengl32.dll", "dinput8.dll"];

    /// <param name="describe">FileVersionInfo description/product of a DLL (null when unknown).</param>
    public static List<ModDll> Detect(string exeDir, Func<string, string?>? describe = null)
    {
        var result = new List<ModDll>();
        if (!Directory.Exists(exeDir)) return result;
        bool Has(string name) => File.Exists(Path.Combine(exeDir, name)) || Directory.Exists(Path.Combine(exeDir, name));
        var reshadeFiles = Has("ReShade.ini") || Has("reshade-shaders") || Has("ReShade.log");
        var enbFiles = Has("enbseries.ini") || Has("enblocal.ini") || Has("enbseries");
        var dxvk = Has("dxvk.conf");

        foreach (var name in WrapperNames)
        {
            foreach (var (path, enabled) in new[] { (Path.Combine(exeDir, name), true), (Path.Combine(exeDir, name + OffSuffix), false) })
            {
                if (!File.Exists(path)) continue;
                var desc = describe?.Invoke(path) ?? "";
                string? kind = desc.Contains("ReShade", StringComparison.OrdinalIgnoreCase) ? "ReShade"
                             : desc.Contains("ENB", StringComparison.OrdinalIgnoreCase) ? "ENB"
                             : desc.Length > 0 ? null // described as something else (DXVK, Special K, loader): leave it
                             : enbFiles && name is "d3d11.dll" or "d3d9.dll" or "d3d8.dll" ? "ENB"
                             : reshadeFiles && !dxvk ? "ReShade"
                             : null;
                if (kind is not null) result.Add(new ModDll(path, kind, enabled));
            }
        }
        return result;
    }

    public static bool AnyEnabled(IEnumerable<ModDll> mods) => mods.Any(m => m.Enabled);

    /// <summary>Renames the wrapper DLLs. Returns how many were switched.</summary>
    public static int Set(IEnumerable<ModDll> mods, bool enable)
    {
        var n = 0;
        foreach (var m in mods.Where(m => m.Enabled != enable))
        {
            var target = enable ? m.Path[..^OffSuffix.Length] : m.Path + OffSuffix;
            if (File.Exists(target)) continue; // never overwrite
            File.Move(m.Path, target);
            n++;
        }
        return n;
    }
}
