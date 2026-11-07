using System.Diagnostics;
using FitzLauncher.Core.Config;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Scanning;

/// <summary>
/// Scans every file of an install once to work out what can be optimised: engine, upscalers,
/// injectors, graphics API hints and the location + current values of editable config files.
/// Pure file-system work; nothing is modified.
/// </summary>
public sealed class GameScanner(ScanContext context)
{
    private static readonly string[] UnrealPlatformDirs = ["Windows", "WindowsNoEditor", "WinGDK", "WinGRTS"];

    public GameScanResult Scan(Game game, CancellationToken ct = default)
    {
        var result = new GameScanResult();
        var root = ResolveInstallRoot(game);
        if (!Directory.Exists(root))
        {
            result.Notes.Add("Install folder not found.");
            return result;
        }

        var files = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            result.FileCount++;
            result.TotalBytes += info.Length;
            files.Add(info.FullName);
        }

        var names = new HashSet<string>(files.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        DetectEngine(root, files, names, game, result);
        DetectUpscalers(files, result);
        DetectInjectors(Path.GetDirectoryName(game.ExePath) ?? root, result);
        DetectGraphicsApi(game.ExePath, names, result);
        LocateConfig(root, game, files, result);
        ReadCurrentSettings(result);
        return result;
    }

    /// <summary>The exe often lives deep inside the install (Binaries\Win64, bin\x64). Walk up to the real root.</summary>
    public static string ResolveInstallRoot(Game game)
    {
        if (!string.IsNullOrEmpty(game.InstallDir) && Directory.Exists(game.InstallDir)) return game.InstallDir;
        var dir = Path.GetDirectoryName(game.ExePath) ?? "";
        return GuessRootFromExe(dir);
    }

    public static string GuessRootFromExe(string exeDir)
    {
        var dir = new DirectoryInfo(exeDir);
        // ...\<Project>\Binaries\Win64\x.exe  → root is the parent of <Project>
        if (dir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) &&
            dir.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent.Parent?.Parent is { } ueRoot)
            return ueRoot.FullName;
        // ...\bin\x64\x.exe or ...\bin\win64\x.exe → root two levels up
        if (dir.Name is "x64" or "win64" or "Win64" or "x86_64" &&
            dir.Parent?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent.Parent is { } binRoot)
            return binRoot.FullName;
        // ...\game\bin\win64 (Source 2)
        if (dir.Parent?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent.Parent?.Name.Equals("game", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent.Parent.Parent is { } s2Root)
            return s2Root.FullName;
        return dir.FullName;
    }

    private void DetectEngine(string root, List<string> files, HashSet<string> names, Game game, GameScanResult r)
    {
        bool Any(Func<string, bool> pred) => files.Any(pred);
        bool HasDir(string relativeSegment) =>
            files.Any(f => f.Contains(Path.DirectorySeparatorChar + relativeSegment + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var paks = files.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) &&
                                    Path.GetDirectoryName(f)!.EndsWith(Path.Combine("Content", "Paks"), StringComparison.OrdinalIgnoreCase)).ToList();
        var shipping = files.FirstOrDefault(f => f.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));

        if (paks.Count > 0 || shipping != null)
        {
            r.Engine = GameEngine.UnrealUnknownVersion;
            // <root>\<Project>\Content\Paks\x.pak
            var project = paks.Select(p => new DirectoryInfo(Path.GetDirectoryName(p)!).Parent?.Parent?.Name)
                              .FirstOrDefault(n => n != null && !n.Equals("Engine", StringComparison.OrdinalIgnoreCase));
            project ??= shipping != null ? Path.GetFileName(shipping)[..^"-Win64-Shipping.exe".Length] : null;
            r.UnrealProjectName = project;
            var major = shipping != null ? TryGetFileMajorVersion(shipping) : null;
            if (major == 5) r.Engine = GameEngine.Unreal5;
            else if (major == 4) r.Engine = GameEngine.Unreal4;
            return;
        }
        if (names.Contains("UnityPlayer.dll") || Any(f => f.EndsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase)))
        { r.Engine = GameEngine.Unity; return; }
        if (names.Contains("engine2.dll") || names.Contains("gameinfo.gi")) { r.Engine = GameEngine.Source2; return; }
        if (names.Contains("gameinfo.txt") && names.Contains("engine.dll")) { r.Engine = GameEngine.Source; return; }
        if (Any(f => f.EndsWith("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase))) { r.Engine = GameEngine.ReEngine; return; }
        if (names.Contains("CrySystem.dll")) { r.Engine = GameEngine.CryEngine; return; }
        if (HasDir(Path.Combine("archive", "pc")) || HasDir("r6") || Any(f => f.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) && f.Contains("content0", StringComparison.OrdinalIgnoreCase)))
        { r.Engine = GameEngine.RedEngine; return; }
        if (Any(f => f.EndsWith(".cas", StringComparison.OrdinalIgnoreCase))) { r.Engine = GameEngine.Frostbite; return; }
        if (Any(f => f.EndsWith(".forge", StringComparison.OrdinalIgnoreCase))) { r.Engine = GameEngine.Anvil; return; }
        if (Any(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))) { r.Engine = GameEngine.CreationEngine; return; }
        if (Any(f => f.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) && HasDir("base")) { r.Engine = GameEngine.IdTech; return; }
        if (HasDir("Packed_DX12") || HasDir("LocalCacheWinGame")) { r.Engine = GameEngine.Decima; return; }
        var exeStem = Path.GetFileNameWithoutExtension(game.ExePath);
        if (names.Contains(exeStem + ".pck")) { r.Engine = GameEngine.Godot; return; }
        r.Engine = GameEngine.Unknown;
    }

    private static int? TryGetFileMajorVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            // Unreal stamps the engine version into ProductVersion on many titles ("++UE5+Release-5.1-CL-...").
            var pv = v.ProductVersion ?? "";
            if (pv.Contains("UE5", StringComparison.OrdinalIgnoreCase) || pv.Contains("Release-5.")) return 5;
            if (pv.Contains("UE4", StringComparison.OrdinalIgnoreCase) || pv.Contains("Release-4.")) return 4;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        return null;
    }

    private static void DetectUpscalers(List<string> files, GameScanResult r)
    {
        foreach (var f in files)
        {
            var n = Path.GetFileName(f).ToLowerInvariant();
            switch (n)
            {
                case "nvngx_dlss.dll":
                    Add(Upscaler.Dlss);
                    try { r.DlssVersion ??= FileVersionInfo.GetVersionInfo(f).FileVersion?.Replace(',', '.').Replace(" ", ""); }
                    catch (Exception e) when (e is IOException or PlatformNotSupportedException) { }
                    break;
                case "nvngx_dlssg.dll": Add(Upscaler.DlssFrameGen); break;
                case "libxess.dll": Add(Upscaler.Xess); break;
                default:
                    if (n.StartsWith("ffx_fsr") || n.StartsWith("amd_fidelityfx") || n.Contains("fsr2") || n.Contains("fsr3"))
                        Add(Upscaler.Fsr);
                    break;
            }
        }
        void Add(Upscaler u) { if (!r.Upscalers.Contains(u)) r.Upscalers.Add(u); }
    }

    private static void DetectInjectors(string exeDir, GameScanResult r)
    {
        if (!Directory.Exists(exeDir)) return;
        bool Has(string name) => File.Exists(Path.Combine(exeDir, name));
        if (Has("ReShade.ini") || Directory.Exists(Path.Combine(exeDir, "reshade-shaders"))) r.Injectors.Add("ReShade");
        if (Has("enbseries.ini") || Has("enblocal.ini")) r.Injectors.Add("ENB");
        if (Has("dxvk.conf")) r.Injectors.Add("DXVK");
        if (Has("SpecialK.ini") || Has("SpecialK64.dll")) r.Injectors.Add("Special K");
        if (r.Injectors.Count == 0 && (Has("dxgi.dll") || Has("d3d11.dll") || Has("d3d9.dll") || Has("dinput8.dll")))
            r.Injectors.Add("DLL wrapper (mod loader/overlay)");
    }

    private static void DetectGraphicsApi(string exePath, HashSet<string> names, GameScanResult r)
    {
        var imports = PeImports.Read(exePath);
        r.UsesVulkan = imports.Contains("vulkan-1.dll");
        r.UsesDx12 = imports.Contains("d3d12.dll") || names.Contains("D3D12Core.dll");
    }

    private void LocateConfig(string root, Game game, List<string> files, GameScanResult r)
    {
        void AddDir(string d)
        {
            if (Directory.Exists(d) && !r.ConfigDirectories.Contains(d, StringComparer.OrdinalIgnoreCase)) r.ConfigDirectories.Add(d);
        }

        if (!string.IsNullOrEmpty(game.CustomConfigDir)) AddDir(game.CustomConfigDir);

        if (r.Engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion)
        {
            var names = new[] { r.UnrealProjectName, game.Name, game.Name.Replace(" ", "") }
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bases = new List<string>();
            foreach (var n in names)
            {
                bases.Add(Path.Combine(context.LocalAppData, n!));
                bases.Add(Path.Combine(context.Documents, "My Games", n!));
                bases.Add(Path.Combine(root, n!));
            }
            foreach (var b in bases)
            foreach (var platform in UnrealPlatformDirs)
            {
                var dir = Path.Combine(b, "Saved", "Config", platform);
                if (!Directory.Exists(dir)) continue;
                AddDir(dir);
                // The platform folder name reveals the major version once the game has been run.
                if (r.Engine == GameEngine.UnrealUnknownVersion)
                    r.Engine = platform == "WindowsNoEditor" ? GameEngine.Unreal4 : GameEngine.Unreal5;
            }
            if (r.ConfigDirectories.Count == 0)
                r.Notes.Add("Unreal config folder not found yet — launch the game once (or set the config folder manually), then scan again.");
        }
        else if (r.Engine == GameEngine.Unity)
        {
            foreach (var boot in files.Where(f => Path.GetFileName(f).Equals("boot.config", StringComparison.OrdinalIgnoreCase)))
                AddDir(Path.GetDirectoryName(boot)!);
        }
        else if (r.Engine is GameEngine.Source or GameEngine.Source2)
        {
            foreach (var cfg in files.Where(f => Path.GetFileName(f).Equals("config.cfg", StringComparison.OrdinalIgnoreCase)
                                              || Path.GetFileName(f).Equals("autoexec.cfg", StringComparison.OrdinalIgnoreCase)))
                AddDir(Path.GetDirectoryName(cfg)!);
        }

        foreach (var dir in r.ConfigDirectories)
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".ini" or ".cfg" or ".config" or ".json" or ".xml" || Path.GetFileName(f) == "boot.config")
                    r.ConfigFiles.Add(f);
            }
        }
    }

    private static void ReadCurrentSettings(GameScanResult r)
    {
        foreach (var file in r.ConfigFiles)
        {
            var name = Path.GetFileName(file);
            try
            {
                if (name.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase))
                {
                    var ini = IniDocument.Load(file);
                    foreach (var (k, v) in ini.GetSection("ScalabilityGroups")) r.CurrentSettings["GUS:" + k] = v;
                    foreach (var (k, v) in ini.GetSection(UnrealKeys.GameUserSettingsSection)) r.CurrentSettings["GUS:" + k] = v;
                }
                else if (name.Equals("Engine.ini", StringComparison.OrdinalIgnoreCase))
                {
                    var ini = IniDocument.Load(file);
                    foreach (var (k, v) in ini.GetSection("SystemSettings")) r.CurrentSettings["Engine:" + k] = v;
                }
                else if (name.Equals("boot.config", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0) r.CurrentSettings["boot:" + line[..eq].Trim()] = line[(eq + 1)..].Trim();
                    }
                }
            }
            catch (IOException) { }
        }
    }
}

public static class UnrealKeys
{
    public const string GameUserSettingsSection = "/Script/Engine.GameUserSettings";
    public const string StreamingSection = "/Script/Engine.StreamingSettings";
}
