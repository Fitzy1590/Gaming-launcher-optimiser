using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace FitzLauncher.App.Services;

/// <summary>
/// Downloads the latest official DXVK release (github.com/doitsujin/dxvk) once, into the launcher's data
/// folder. Only the x64/x32 DLLs are extracted; nothing is installed system-wide.
/// </summary>
public sealed class DxvkService(string root)
{
    public string Root { get; } = root;
    private string VersionFile => Path.Combine(Root, "version.txt");

    public bool IsReady => File.Exists(Path.Combine(Root, "x64", "d3d11.dll")) && File.Exists(Path.Combine(Root, "x32", "d3d9.dll"));
    public string? Version => File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null;

    public async Task<(bool ok, string message)> EnsureAsync(CancellationToken ct = default)
    {
        if (IsReady) return (true, $"DXVK {Version} ready");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FitzLauncher");
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/doitsujin/dxvk/releases/latest", ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
            var url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString()))
                .FirstOrDefault(a => a.name.StartsWith("dxvk-", StringComparison.Ordinal) && a.name.EndsWith(".tar.gz", StringComparison.Ordinal)
                                     && !a.name.Contains("native", StringComparison.OrdinalIgnoreCase)).url;
            if (url is null) return (false, "Couldn't find the DXVK download on GitHub.");

            var staging = Root + ".partial";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await using (var gz = new GZipStream(await http.GetStreamAsync(url, ct), CompressionMode.Decompress))
            await using (var tar = new TarReader(gz))
            {
                while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                {
                    // dxvk-2.x/x64/d3d11.dll → x64/d3d11.dll; anything else is skipped.
                    var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || parts.Length != 3) continue;
                    if (parts[1] is not ("x64" or "x32") || !parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts[2].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    var dest = Path.Combine(staging, parts[1], parts[2]);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await entry.ExtractToFileAsync(dest, overwrite: true, ct);
                }
            }
            if (!File.Exists(Path.Combine(staging, "x64", "d3d11.dll"))) return (false, "The DXVK download didn't contain the expected files.");
            File.WriteAllText(Path.Combine(staging, "version.txt"), tag);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.Move(staging, Root);
            return (true, $"Downloaded DXVK {tag}");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or KeyNotFoundException or TaskCanceledException or UnauthorizedAccessException)
        {
            return (false, $"Couldn't download DXVK: {e.Message}");
        }
    }
}
