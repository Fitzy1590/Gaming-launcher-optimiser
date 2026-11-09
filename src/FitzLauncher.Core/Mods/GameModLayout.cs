using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Mods;

public enum ModFramework { Generic, Bethesda, Unreal, Unity, Cyberpunk, Witcher3 }

/// <summary>
/// How a game takes mods: engine family, where mod files go, and which mod loaders are installed.
/// All paths are relative to the game root, with '/' separators.
/// </summary>
public sealed partial class GameModLayout
{
    public ModFramework Framework { get; init; }
    public string ExeDir { get; init; } = "";
    /// <summary>Bethesda "Data" folder.</summary>
    public string? DataDir { get; init; }
    /// <summary>Unreal "Project/Content/Paks/~mods".</summary>
    public string? PaksModsDir { get; init; }
    /// <summary>UE4SS Lua/C++ mods folder, when UE4SS is installed.</summary>
    public string? Ue4ssModsDir { get; init; }
    /// <summary>Loaders found on disk: SKSE, F4SE, BepInEx, MelonLoader, UE4SS, ASI Loader, Cyber Engine Tweaks…</summary>
    public HashSet<string> Loaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Game runtime a script extender was built for ("1.6.1170"), from its DLL name.</summary>
    public string? ScriptExtenderRuntime { get; init; }
    public string? ScriptExtenderName { get; init; }
    public bool HasAntiCheat { get; init; }
    /// <summary>Unreal IoStore game (.utoc/.ucas): pak-only mods usually need the matching .utoc/.ucas too.</summary>
    public bool UsesIoStore { get; init; }
    /// <summary>Folder name under %LOCALAPPDATA% holding Plugins.txt (Bethesda), and whether it uses the "*" format.</summary>
    public string? PluginsFolderName { get; init; }
    public bool PluginsStarFormat { get; init; }

    public static readonly string[] ProxyDlls = ["dxgi.dll", "d3d11.dll", "d3d9.dll", "d3d12.dll", "dinput8.dll", "version.dll", "winmm.dll", "xinput1_3.dll", "xinput1_4.dll", "dsound.dll", "winhttp.dll"];

    [GeneratedRegex(@"^(skse64|skse|sksevr|f4se|f4sevr|sfse|obse64|obse|nvse|fose)_(\d+(?:_\d+){1,3})\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtenderDll();

    private static readonly (string exe, string folder, bool star, string? extender)[] Bethesda =
    [
        ("SkyrimSE.exe", "Skyrim Special Edition", true, "SKSE"),
        ("SkyrimVR.exe", "Skyrim VR", true, "SKSE"),
        ("TESV.exe", "Skyrim", false, "SKSE"),
        ("Fallout4.exe", "Fallout4", true, "F4SE"),
        ("Fallout4VR.exe", "Fallout4VR", true, "F4SE"),
        ("Starfield.exe", "Starfield", true, "SFSE"),
        ("FalloutNV.exe", "FalloutNV", false, "NVSE"),
        ("Fallout3.exe", "Fallout3", false, "FOSE"),
        ("Oblivion.exe", "Oblivion", false, "OBSE"),
    ];

    public static GameModLayout Detect(string gameRoot, string exePath)
    {
        var exeDir = Rel(gameRoot, Path.GetDirectoryName(exePath) ?? gameRoot);
        var loaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Has(string rel) => File.Exists(Path.Combine(gameRoot, rel)) || Directory.Exists(Path.Combine(gameRoot, rel));
        string InExe(string name) => exeDir.Length == 0 ? name : $"{exeDir}/{name}";

        var exeName = Path.GetFileName(exePath);
        var beth = Bethesda.FirstOrDefault(b => b.exe.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        var framework = ModFramework.Generic;
        string? dataDir = null, paksMods = null, ue4ssMods = null, runtime = null, extenderName = null;
        var ioStore = false;

        if (beth.exe is not null || (Has("Data") && Directory.EnumerateFiles(Path.Combine(gameRoot, "Data"), "*.esm").Any()))
        {
            framework = ModFramework.Bethesda;
            dataDir = "Data";
        }

        // Script extenders: skse64_loader.exe + skse64_1_6_1170.dll
        foreach (var f in SafeFiles(Path.Combine(gameRoot, exeDir)))
        {
            var m = ExtenderDll().Match(Path.GetFileName(f));
            if (!m.Success) continue;
            extenderName = m.Groups[1].Value.ToUpperInvariant() switch { "SKSE64" or "SKSEVR" => "SKSE", "F4SEVR" => "F4SE", "OBSE64" => "OBSE", var x => x };
            runtime = m.Groups[2].Value.Replace('_', '.');
            loaders.Add(extenderName);
        }

        if (Has("Cyberpunk2077.exe") || Has("bin/x64/Cyberpunk2077.exe")) framework = ModFramework.Cyberpunk;
        if (Has("bin/x64/plugins/cyber_engine_tweaks")) loaders.Add("Cyber Engine Tweaks");
        if (Has("red4ext")) loaders.Add("RED4ext");
        if (Has("engine/tools/scc.exe")) loaders.Add("redscript");
        if (Has("red4ext/plugins/ArchiveXL")) loaders.Add("ArchiveXL");
        if (Has("red4ext/plugins/TweakXL")) loaders.Add("TweakXL");
        if (Has("red4ext/plugins/Codeware")) loaders.Add("Codeware");

        // The Witcher 3 (classic and next-gen DX11/DX12 exes): mods in mods/modXxx, DLC in dlc/dlcXxx
        if (Has("bin/x64/witcher3.exe") || Has("bin/x64_dx12/witcher3.exe")) framework = ModFramework.Witcher3;

        // Unity
        if (Has(InExe("UnityPlayer.dll")) || SafeDirs(Path.Combine(gameRoot, exeDir)).Any(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, "globalgamemanagers"))))
            framework = framework == ModFramework.Generic ? ModFramework.Unity : framework;
        if (Has("BepInEx/core")) loaders.Add("BepInEx");
        if (Has("MelonLoader")) loaders.Add("MelonLoader");

        // Unreal: <Project>/Content/Paks
        foreach (var dir in SafeDirs(gameRoot))
        {
            var paks = Path.Combine(dir, "Content", "Paks");
            if (!Directory.Exists(paks) || Path.GetFileName(dir).Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
            framework = framework == ModFramework.Generic ? ModFramework.Unreal : framework;
            var project = Path.GetFileName(dir);
            paksMods = $"{project}/Content/Paks/~mods";
            ioStore = SafeFiles(paks).Any(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            var win64 = $"{project}/Binaries/Win64";
            if (Has($"{win64}/ue4ss/UE4SS.dll")) { loaders.Add("UE4SS"); ue4ssMods = $"{win64}/ue4ss/Mods"; }
            else if (Has($"{win64}/UE4SS.dll")) { loaders.Add("UE4SS"); ue4ssMods = $"{win64}/Mods"; }
            break;
        }

        // ASI loader (Ultimate ASI Loader ships as one of the proxy DLLs next to the exe)
        var exeFiles = SafeFiles(Path.Combine(gameRoot, exeDir)).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (exeFiles.Any(f => f!.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)) || Has(InExe("scripts")) && ProxyDlls.Any(d => exeFiles.Contains(d)))
            loaders.Add("ASI Loader");
        if (Has(InExe("ScriptHookV.dll"))) loaders.Add("Script Hook V");
        if (Has(InExe("reframework"))) loaders.Add("REFramework");

        var antiCheat = Has("EasyAntiCheat") || Has(InExe("EasyAntiCheat")) || Has("BattlEye") || Has(InExe("BattlEye")) || Has(InExe("start_protected_game.exe"));

        return new GameModLayout
        {
            Framework = framework, ExeDir = exeDir, DataDir = dataDir, PaksModsDir = paksMods, Ue4ssModsDir = ue4ssMods, Loaders = loaders,
            ScriptExtenderRuntime = runtime, ScriptExtenderName = extenderName, HasAntiCheat = antiCheat, UsesIoStore = ioStore,
            PluginsFolderName = beth.folder, PluginsStarFormat = beth.star,
        };
    }

    public static string Rel(string root, string path)
    {
        var r = Path.GetRelativePath(root, path).Replace('\\', '/');
        return r == "." ? "" : r;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
