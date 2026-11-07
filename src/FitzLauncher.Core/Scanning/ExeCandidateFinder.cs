using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Scanning;

/// <summary>
/// Picks the most likely game executable inside a folder (used by "Add folder" and when the user
/// picks a folder instead of an exe). Skips installers, redistributables, crash reporters, etc.
/// </summary>
public static partial class ExeCandidateFinder
{
    [GeneratedRegex(@"(?i)^(unins\d*|setup|install(er)?|vc_?redist.*|vcredist.*|dxsetup|dxwebsetup|dotnet.*|oalinst|physx.*|uerprereq.*|ueprereqsetup.*|crashreport.*|crashpad.*|.*crashhandler.*|unitycrashhandler.*|easyanticheat.*|eac.*setup|be(service|setup).*|redist.*|launcherpatcher|updater|update|patch(er)?|config(urator)?|settings|cefsharp.*|quicksfv|7z.*|unrar|.*helper|touchup|cleanup|activation|register|dxdiag|vulkan.*)\.exe$")]
    private static partial Regex NotAGame();

    [GeneratedRegex(@"(?i)[\\/](redist|_commonredist|directx|dotnet|vcredist|__installer|support|installers?|_redist|prereq\w*|easyanticheat|battleye|tools?|crashreport\w*|engine[\\/]extras)[\\/]")]
    private static partial Regex NotAGameDir();

    public static IReadOnlyList<string> FindCandidates(string folder, int maxDepth = 4)
    {
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var cleanFolder = Normalise(GameNameCleaner.Clean(folderName));

        return Directory.EnumerateFiles(folder, "*.exe", options)
            .Where(p => !NotAGame().IsMatch(Path.GetFileName(p)))
            .Where(p => !NotAGameDir().IsMatch(p[folder.Length..].Replace('/', '\\') + "\\"))
            .Select(p => (path: p, score: Score(p, folder, cleanFolder)))
            .OrderByDescending(x => x.score)
            .Select(x => x.path)
            .ToList();
    }

    public static string? FindBest(string folder) => FindCandidates(folder).FirstOrDefault();

    private static int Score(string path, string root, string cleanFolder)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var score = 0;
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }

        // The real binary is usually big.
        score += (int)Math.Min(size / (1024 * 1024), 150);
        // Unreal's real binary — but prefer the bootstrapper in the root (it sets the working dir).
        if (name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) score += 20;
        var depth = path[root.Length..].Count(c => c is '\\' or '/');
        score -= depth * 8;
        if (depth <= 1) score += 40;
        // Name matches folder name → very likely.
        var cleanName = Normalise(GameNameCleaner.Clean(name));
        if (cleanName.Length > 2 && cleanFolder.Length > 2 &&
            (cleanFolder.Contains(cleanName) || cleanName.Contains(cleanFolder))) score += 60;
        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase)) score -= 25;
        if (name.Contains("server", StringComparison.OrdinalIgnoreCase)) score -= 40;
        if (name.Contains("editor", StringComparison.OrdinalIgnoreCase)) score -= 40;
        if (name.EndsWith("32", StringComparison.Ordinal)) score -= 10;
        return score;
    }

    private static string Normalise(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
