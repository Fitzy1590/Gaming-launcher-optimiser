using System.Diagnostics;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.App.Services;

/// <summary>Turns an exe, shortcut or folder the user picked/dropped into a library entry.</summary>
public static class GameImporter
{
    public static Game? FromPath(string path)
    {
        if (Directory.Exists(path))
        {
            var exe = ExeCandidateFinder.FindBest(path);
            if (exe is null) return null;
            return Create(exe, Path.GetFileName(path.TrimEnd('\\')));
        }
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var (target, args) = ResolveShortcut(path);
            if (target is null || !File.Exists(target)) return null;
            var g = Create(target, Path.GetFileNameWithoutExtension(path));
            g.Arguments = args ?? "";
            return g;
        }
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return Create(path, null);
        return null;
    }

    private static Game Create(string exe, string? nameHint)
    {
        var root = GameScanner.GuessRootFromExe(Path.GetDirectoryName(exe)!);
        var name = ProductName(exe) ?? GameNameCleaner.Clean(nameHint ?? Path.GetFileName(root));
        return FromExe(exe, name);
    }

    /// <summary>A library entry for an exe the user has already chosen (Scan game folders window).</summary>
    public static Game FromExe(string exe, string name) =>
        new() { Name = name, ExePath = exe, InstallDir = GameScanner.GuessRootFromExe(Path.GetDirectoryName(exe)!) };

    /// <summary>Many games set a proper ProductName ("ELDEN RING™"); engines and tools don't count.</summary>
    public static string? ProductName(string exe)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Replace("™", "").Replace("®", "").Trim();
            if (!string.IsNullOrWhiteSpace(product) && product.Length > 2 &&
                !product.Contains("Unreal", StringComparison.OrdinalIgnoreCase) &&
                !product.Contains("Unity", StringComparison.OrdinalIgnoreCase) &&
                !product.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                !product.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
                return product;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Resolves a .lnk with the Windows Script Host COM object (always present on Windows).</summary>
    private static (string? target, string? args) ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return (null, null);
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            return ((string)sc.TargetPath, (string)sc.Arguments);
        }
        catch (Exception) { return (null, null); }
    }
}
