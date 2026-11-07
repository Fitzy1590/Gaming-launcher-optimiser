using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Scanning;

public enum FoundGameStatus { New, AlreadyInLibrary, NotInstalled, NoExe }

/// <summary>One candidate game found in a scanned folder, for the user to review before adding.</summary>
public sealed class FoundGame
{
    public string Folder { get; init; } = "";
    public string Name { get; set; } = "";
    /// <summary>Exe candidates, best first. The user can pick another.</summary>
    public List<string> Candidates { get; init; } = [];
    public string? Exe { get; set; }
    public FoundGameStatus Status { get; init; }
    public string? Note { get; init; }
    /// <summary>Selected by default only when it's a new, installed game.</summary>
    public bool SuggestAdd => Status == FoundGameStatus.New && Exe is not null;
}

/// <summary>
/// Scans a games folder for the "Scan game folders" window. If the picked folder itself contains a game
/// exe it's treated as one game; otherwise each sub-folder is a game. Repack installers that haven't been
/// run yet (FitGirl/DODI-style setup.exe + .bin archives) are recognised and not offered by default.
/// Nothing is added until the user confirms.
/// </summary>
public static partial class LibraryFolderScanner
{
    [GeneratedRegex(@"(?i)^(setup|install|installer)[\w\- ]*\.exe$")]
    private static partial Regex SetupExe();

    [GeneratedRegex(@"(?i)(^fg-.*\.bin$|^setup-.*\.bin$|\.dodi$|^data\d*\.bin$|\.bin$|^.*-\d+\.bin$)")]
    private static partial Regex RepackArchive();

    public static List<FoundGame> Scan(string root, IReadOnlyCollection<string> knownExePaths, IReadOnlyCollection<string> knownInstallDirs,
        Func<string, string?>? productName = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<FoundGame>();
        if (!Directory.Exists(root)) return result;

        var folders = Directory.EnumerateFiles(root, "*.exe").Any(f => !SetupExe().IsMatch(Path.GetFileName(f)))
            ? [root]
            : SafeSubfolders(root);

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Path.GetFileName(folder));
            result.Add(Examine(folder, knownExePaths, knownInstallDirs, productName));
        }
        return result;
    }

    public static FoundGame Examine(string folder, IReadOnlyCollection<string> knownExePaths, IReadOnlyCollection<string> knownInstallDirs, Func<string, string?>? productName = null)
    {
        var folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var cleanName = GameNameCleaner.Clean(folderName);

        if (IsUninstalledRepack(folder))
        {
            return new FoundGame
            {
                Folder = folder, Name = cleanName, Status = FoundGameStatus.NotInstalled,
                Note = "Repack installer, not installed yet. Run its setup.exe, then scan the install folder.",
            };
        }

        var candidates = ExeCandidateFinder.FindCandidates(folder).Take(8).ToList();
        if (candidates.Count == 0)
            return new FoundGame { Folder = folder, Name = cleanName, Status = FoundGameStatus.NoExe, Note = "No game exe found. If this is a folder of games, scan it directly." };

        var known = knownExePaths.Any(k => candidates.Any(c => c.Equals(k, StringComparison.OrdinalIgnoreCase))) ||
                    knownInstallDirs.Any(d => d.TrimEnd('\\', '/').Equals(folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

        var name = cleanName;
        if (productName?.Invoke(candidates[0]) is { Length: > 2 } product) name = product;

        return new FoundGame
        {
            Folder = folder, Name = name, Candidates = candidates, Exe = candidates[0],
            Status = known ? FoundGameStatus.AlreadyInLibrary : FoundGameStatus.New,
            Note = known ? "Already in your library." : null,
        };
    }

    /// <summary>setup.exe plus repack archives and no runnable game exe = not installed yet.</summary>
    public static bool IsUninstalledRepack(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToList();
            var hasSetup = files.Any(f => SetupExe().IsMatch(f!));
            var archives = files.Count(f => RepackArchive().IsMatch(f!));
            if (!hasSetup || archives == 0) return false;
            // An installed game folder can also contain a setup.exe; only call it an installer if there's no real exe.
            return ExeCandidateFinder.FindCandidates(folder, maxDepth: 2).Count == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<string> SafeSubfolders(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden) &&
                            !Path.GetFileName(d).StartsWith('$') && !Path.GetFileName(d).Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
