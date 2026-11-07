using System.Text;

namespace FitzLauncher.Core.Scanning;

/// <summary>
/// Looks for marker strings inside a binary (ASCII and UTF-16), streaming it in 4 MB chunks.
/// Used to spot features compiled into the exe, such as NVIDIA Reflex or AMD Anti-Lag 2, that ship
/// no DLL of their own. A 150 MB exe scans in well under a second.
/// </summary>
public static class BinaryFeatureScanner
{
    public const long MaxBytes = 600L * 1024 * 1024;

    public static HashSet<string> Find(string path, IReadOnlyCollection<string> markers)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path) || markers.Count == 0) return found;

        var patterns = markers.SelectMany(m => new[]
        {
            (marker: m, bytes: Encoding.ASCII.GetBytes(m)),
            (marker: m, bytes: Encoding.Unicode.GetBytes(m)),
        }).ToList();
        var overlap = patterns.Max(p => p.bytes.Length);
        const int chunk = 4 * 1024 * 1024;
        var buffer = new byte[chunk + overlap];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            var carried = 0;
            long total = 0;
            int read;
            while (total < MaxBytes && (read = fs.Read(buffer, carried, chunk)) > 0)
            {
                total += read;
                var span = buffer.AsSpan(0, carried + read);
                foreach (var (marker, bytes) in patterns)
                    if (!found.Contains(marker) && span.IndexOf(bytes) >= 0) found.Add(marker);
                if (found.Count == markers.Count) break;
                // Keep the tail so a marker split across chunks is still found.
                carried = Math.Min(overlap, span.Length);
                span[^carried..].CopyTo(buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }
}
