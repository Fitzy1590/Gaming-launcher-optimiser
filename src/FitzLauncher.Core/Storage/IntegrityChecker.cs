using System.Security.Cryptography;
using System.Text.Json;

namespace FitzLauncher.Core.Storage;

public enum IntegrityIssueKind { Missing, Changed, Corrupt }
public sealed record IntegrityIssue(string RelativePath, IntegrityIssueKind Kind);
public sealed record IntegrityReport(int Checked, List<IntegrityIssue> Issues, string Method)
{
    public bool Ok => Issues.Count == 0;
    public string Summary => Ok ? $"All {Checked:N0} files OK ({Method})."
        : $"{Issues.Count(i => i.Kind == IntegrityIssueKind.Missing)} missing, {Issues.Count(i => i.Kind != IntegrityIssueKind.Missing)} changed or corrupt, out of {Checked:N0} files ({Method}).";
}

/// <summary>
/// Repack integrity:
/// <list type="bullet">
/// <item>Checksum files that came with the repack (<c>*.md5</c>, <c>*.sfv</c>), e.g. FitGirl's MD5 folder for
/// installer archives, so a bad download is caught before installing.</item>
/// <item>A baseline snapshot of an installed game (path + size, optionally SHA-256), so later problems show
/// up: files removed by antivirus, damaged by a bad mod, or truncated on an SD card.</item>
/// </list>
/// </summary>
public static class IntegrityChecker
{
    public sealed class Baseline
    {
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public bool Hashed { get; set; }
        public Dictionary<string, BaselineFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record BaselineFile(long Size, string? Sha256);

    /// <summary>Finds .md5/.sfv checksum lists in a folder (repack installers keep them in an MD5 sub-folder).</summary>
    public static List<string> FindChecksumFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Where(f => f.EndsWith(".md5", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

    /// <summary>Verifies every file listed in a .md5 ("hash *name") or .sfv ("name CRC32") file.</summary>
    public static IntegrityReport VerifyChecksumFile(string checksumFile, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var baseDir = Path.GetDirectoryName(checksumFile)!;
        var sfv = checksumFile.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase);
        var entries = new List<(string path, string hash)>();
        foreach (var raw in File.ReadLines(checksumFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (sfv)
            {
                var sp = line.LastIndexOf(' ');
                if (sp > 0) entries.Add((line[..sp].Trim(), line[(sp + 1)..].Trim()));
            }
            else
            {
                var sp = line.IndexOf(' ');
                if (sp > 0) entries.Add((line[(sp + 1)..].TrimStart(' ', '*'), line[..sp].Trim()));
            }
        }

        var issues = new List<IntegrityIssue>();
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (rel, expected) = entries[i];
            var path = ResolveListedPath(baseDir, rel);
            progress?.Report((i, entries.Count));
            if (path is null) { issues.Add(new IntegrityIssue(rel, IntegrityIssueKind.Missing)); continue; }
            var actual = sfv ? Crc32Hex(path) : Md5Hex(path);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) issues.Add(new IntegrityIssue(rel, IntegrityIssueKind.Corrupt));
        }
        progress?.Report((entries.Count, entries.Count));
        return new IntegrityReport(entries.Count, issues, sfv ? "SFV / CRC32" : "MD5");
    }

    /// <summary>
    /// Paths in checksum lists are relative to the list or to its parent (FitGirl keeps the list in
    /// an MD5 sub-folder and writes paths like "..\fg-01.bin" or "fg-01.bin").
    /// </summary>
    private static string? ResolveListedPath(string baseDir, string rel)
    {
        rel = rel.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var dir in new[] { baseDir, Path.GetDirectoryName(baseDir) })
        {
            if (dir is null) continue;
            var p = Path.GetFullPath(Path.Combine(dir, rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static Baseline CreateBaseline(string installDir, bool hash, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(installDir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        var b = new Baseline { Hashed = hash };
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            var rel = Path.GetRelativePath(installDir, f);
            if (IsVolatile(rel)) continue;
            b.Files[rel] = new BaselineFile(new FileInfo(f).Length, hash ? Sha256Hex(f) : null);
            if (i % 50 == 0) progress?.Report((i, files.Count));
        }
        progress?.Report((files.Count, files.Count));
        return b;
    }

    /// <summary>Quick = missing files and size changes (seconds). Full = also re-hashes (needs a hashed baseline).</summary>
    public static IntegrityReport VerifyBaseline(string installDir, Baseline baseline, bool full, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var issues = new List<IntegrityIssue>();
        var i = 0;
        foreach (var (rel, expected) in baseline.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (i++ % 50 == 0) progress?.Report((i, baseline.Files.Count));
            var path = Path.Combine(installDir, rel);
            if (!File.Exists(path)) { issues.Add(new IntegrityIssue(rel, IntegrityIssueKind.Missing)); continue; }
            if (new FileInfo(path).Length != expected.Size) { issues.Add(new IntegrityIssue(rel, IntegrityIssueKind.Changed)); continue; }
            if (full && expected.Sha256 is { } h && !Sha256Hex(path).Equals(h, StringComparison.OrdinalIgnoreCase))
                issues.Add(new IntegrityIssue(rel, IntegrityIssueKind.Corrupt));
        }
        progress?.Report((baseline.Files.Count, baseline.Files.Count));
        return new IntegrityReport(baseline.Files.Count, issues, full && baseline.Hashed ? "full SHA-256" : "quick: presence and size");
    }

    public static void SaveBaseline(string path, Baseline b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(b, Json.Compact));
    }

    public static Baseline? LoadBaseline(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), Json.Compact); }
        catch (JsonException) { return null; }
    }

    // Logs, caches and config change on their own; they're not damage.
    private static bool IsVolatile(string rel)
    {
        var r = rel.Replace('\\', '/').ToLowerInvariant();
        return r.EndsWith(".log") || r.EndsWith(".tmp") || r.EndsWith(".ini") || r.EndsWith(".cfg") || r.Contains("/saved/") ||
               r.Contains("/cache/") || r.Contains("shadercache") || r.EndsWith(".dmp") || r.Contains("crashes/");
    }

    private static string Md5Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(fs));
    }

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 (IEEE), as used by .sfv files, written as 8 upper-case hex digits.</summary>
    internal static string Crc32Hex(string path)
    {
        var crc = 0xFFFFFFFFu;
        var buffer = new byte[1 << 16];
        using var fs = File.OpenRead(path);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return (crc ^ 0xFFFFFFFFu).ToString("X8");
    }
}
