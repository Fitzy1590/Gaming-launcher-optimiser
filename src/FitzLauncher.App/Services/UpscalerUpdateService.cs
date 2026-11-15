using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.App.Services;

/// <summary>
/// Keeps a library of the newest upscaler DLLs from their makers' official GitHub repositories (Intel XeSS, AMD
/// FidelityFX FSR 3.1, NVIDIA DLSS on PCs with an NVIDIA GPU) and swaps them into every game that ships an older
/// copy, once a day. Each swap is a backed-up, revertible change; games with anti-cheat, hidden and running games
/// are skipped. Also fetches the latest OptiScaler release for one-click installs.
/// </summary>
public sealed partial class UpscalerUpdateService(AppServices s)
{
    public string LibraryDir => Path.Combine(s.Paths.Root, "dll-library");
    public string OptiScalerDir => Path.Combine(s.Paths.Root, "optiscaler");

    private const string Raw = "https://raw.githubusercontent.com";
    private sealed record DllSource(UpscalerDllKind Kind, string Url);

    private async Task<List<DllSource>> SourcesAsync(CancellationToken ct)
    {
        var list = new List<DllSource> { new(UpscalerDllKind.XeSS, $"{Raw}/intel/xess/main/bin/libxess.dll") };
        // FSR 3.1: the newest v1.x FidelityFX SDK release ships the game-facing amd_fidelityfx_dx12/vk.dll.
        var fsrTag = await NewestFsrTagAsync(ct) ?? "v1.1.4";
        list.Add(new(UpscalerDllKind.FsrDx12, $"{Raw}/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/{fsrTag}/PrebuiltSignedDLL/amd_fidelityfx_dx12.dll"));
        list.Add(new(UpscalerDllKind.FsrVulkan, $"{Raw}/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/{fsrTag}/PrebuiltSignedDLL/amd_fidelityfx_vk.dll"));
        // DLSS only matters on NVIDIA RTX GPUs.
        if (s.Hardware?.PrimaryGpu?.Vendor == GpuVendor.Nvidia)
            foreach (var (k, f) in new[] { (UpscalerDllKind.DlssSuperResolution, "nvngx_dlss.dll"), (UpscalerDllKind.DlssFrameGeneration, "nvngx_dlssg.dll"), (UpscalerDllKind.DlssRayReconstruction, "nvngx_dlssd.dll") })
                list.Add(new(k, $"{Raw}/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/{f}"));
        return list;
    }

    [GeneratedRegex(@"^v1\.(\d+)\.(\d+)$")] private static partial Regex FsrTag();

    private async Task<string?> NewestFsrTagAsync(CancellationToken ct)
    {
        try
        {
            var tags = JsonNode.Parse(await s.StoreHttp.GetStringAsync("https://api.github.com/repos/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tags?per_page=100", ct))?.AsArray() ?? [];
            var candidates = tags.Select(t => t?["name"]?.GetValue<string>() ?? "").Select(n => (n, m: FsrTag().Match(n))).Where(x => x.m.Success)
                .OrderByDescending(x => int.Parse(x.m.Groups[1].Value)).ThenByDescending(x => int.Parse(x.m.Groups[2].Value)).Select(x => x.n);
            foreach (var tag in candidates.Take(4))
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, $"{Raw}/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/{tag}/PrebuiltSignedDLL/amd_fidelityfx_dx12.dll");
                using var res = await s.StoreHttp.SendAsync(head, ct);
                if (res.IsSuccessStatusCode) return tag;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        return null;
    }

    /// <summary>Downloads any DLL whose official copy changed (by ETag). Returns what's new.</summary>
    public async Task<List<string>> RefreshLibraryAsync(CancellationToken ct = default)
    {
        var updated = new List<string>();
        foreach (var src in await SourcesAsync(ct))
        {
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, src.Url);
                using var hres = await s.StoreHttp.SendAsync(head, ct);
                if (!hres.IsSuccessStatusCode) continue;
                var etag = hres.Headers.ETag?.Tag ?? hres.Content.Headers.ContentLength?.ToString() ?? "";
                if (s.Settings.UpscalerEtags.TryGetValue(src.Url, out var known) && known == etag && HasKind(src.Kind)) continue;

                var temp = Path.Combine(Path.GetTempPath(), "fitz-dll-" + Guid.NewGuid().ToString("N") + ".dll");
                await using (var stream = await s.StoreHttp.GetStreamAsync(src.Url, ct))
                await using (var file = File.Create(temp)) await stream.CopyToAsync(file, ct);
                var vi = FileVersionInfo.GetVersionInfo(temp);
                var version = $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
                if (vi.FileMajorPart == 0 && vi.FileMinorPart == 0) { File.Delete(temp); continue; } // not a real DLL (e.g. a Git LFS pointer)
                var dest = Path.Combine(LibraryDir, src.Kind.ToString(), version, UpscalerLibrary.FileName(src.Kind));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) { File.Move(temp, dest); updated.Add($"{UpscalerLibrary.Label(src.Kind)} {version}"); }
                else File.Delete(temp);
                s.Settings.UpscalerEtags[src.Url] = etag;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                s.Log.Write($"Upscaler download {src.Kind}: {e.Message}");
            }
        }
        s.Settings.LastUpscalerCheck = DateTimeOffset.Now;
        s.SaveSettings();
        return updated;
    }

    private bool HasKind(UpscalerDllKind k) => Directory.Exists(Path.Combine(LibraryDir, k.ToString())) && Directory.EnumerateFiles(Path.Combine(LibraryDir, k.ToString()), "*.dll", SearchOption.AllDirectories).Any();

    /// <summary>Newest version of each kind in the library (for Settings).</summary>
    public List<string> LibrarySummary() =>
        Enum.GetValues<UpscalerDllKind>().Where(HasKind)
            .Select(k => $"{UpscalerLibrary.Label(k)} {Directory.EnumerateDirectories(Path.Combine(LibraryDir, k.ToString())).Select(Path.GetFileName).OrderByDescending(v => UpscalerLibrary.TryVersion(v, out var x) ? x : new Version()).FirstOrDefault()}")
            .ToList();

    /// <summary>Swaps newer DLLs into games. Returns (games updated, DLLs swapped).</summary>
    public (int games, int dlls) ApplyToLibrary(Func<Game, bool> isRunning, IEnumerable<Game>? only = null)
    {
        var best = s.BestUpscalerDlls();
        int games = 0, dlls = 0;
        foreach (var g in (only ?? s.Library.Games).Where(g => !g.Hidden && g.LastScan is not null && !isRunning(g)).ToList())
        {
            if (GameModLayout.Detect(GameScanner.ResolveInstallRoot(g), g.ExePath).HasAntiCheat) continue;
            var swapped = 0;
            foreach (var tweak in UpscalerLibrary.Upgrades(g.LastScan!, best))
            {
                // Automatic swaps stay within a major version for XeSS (1.x and 2.x integrations can differ);
                // bigger jumps stay a manual suggestion on the game page.
                var target = tweak.Actions.OfType<FileReplaceAction>().FirstOrDefault();
                var current = target is null ? null : g.LastScan!.UpscalerDlls.FirstOrDefault(d => d.Path.Equals(target.TargetPath, StringComparison.OrdinalIgnoreCase));
                if (current is { Kind: UpscalerDllKind.XeSS } && UpscalerLibrary.TryVersion(current.Version, out var cv) && UpscalerLibrary.TryVersion(target!.NewVersion, out var nv) && cv.Major != nv.Major)
                    continue;
                var r = s.Tweaks.Apply(g.AppliedChanges, tweak, g);
                if (!r.Success) { s.Log.Write($"[{g.Name}] DLL update: {r.Message}"); continue; }
                swapped++;
                // Record the new version so tomorrow's check doesn't swap it again.
                foreach (var a in tweak.Actions.OfType<FileReplaceAction>())
                    if (g.LastScan!.UpscalerDlls.FirstOrDefault(d => d.Path.Equals(a.TargetPath, StringComparison.OrdinalIgnoreCase)) is { } d) d.Version = a.NewVersion;
            }
            if (swapped > 0) { games++; dlls += swapped; }
        }
        s.SaveLibrary();
        return (games, dlls);
    }

    /// <summary>The newest OptiScaler release (official GitHub), downloaded and unpacked once per version.</summary>
    public async Task<string> EnsureOptiScalerAsync(CancellationToken ct = default)
    {
        var rel = JsonNode.Parse(await s.StoreHttp.GetStringAsync("https://api.github.com/repos/optiscaler/OptiScaler/releases/latest", ct))!;
        var tag = ModInstallPlanner.SafeName(rel["tag_name"]?.GetValue<string>() ?? "latest");
        var dir = Path.Combine(OptiScalerDir, tag);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "OptiScaler.dll", SearchOption.AllDirectories).Any()) return dir;
        var asset = (rel["assets"]?.AsArray() ?? []).Select(a => (name: a?["name"]?.GetValue<string>() ?? "", url: a?["browser_download_url"]?.GetValue<string>()))
            .FirstOrDefault(a => a.url is not null && (a.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
        if (asset.url is null) throw new Mods.StoreException("The latest OptiScaler release has no download attached.");
        var archive = await s.Mods.DownloadAsync(s.StoreHttp, asset.url, asset.name, null, ct);
        var entries = ModArchive.ListEntries(archive).Where(ModInstallPlanner.IsSafeRelative).Select(e => new PlannedFile(e, e)).ToList();
        await Task.Run(() => ModArchive.Extract(archive, entries, dir), ct);
        return dir;
    }

    public string AmdNrDir => Path.Combine(s.Paths.Root, "amdnr");

    /// <summary>
    /// The newest AMD NR release (3zwr1's own GitHub Releases, pre-releases included), downloaded once per tag into the
    /// Fitz folder, never the game folder. Checks size and the SHA-256 GitHub publishes before unpacking; a release without
    /// a digest, or one missing any of the files that must travel together, is refused. Returns the unpacked folder.
    /// </summary>
    public async Task<string> EnsureAmdNrAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string body;
        try { body = await s.StoreHttp.GetStringAsync(AmdNrRelease.ReleasesApi, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new Mods.StoreException($"Couldn't reach GitHub to look for AMD NR ({e.Message})."); }
        var pick = AmdNrRelease.Pick(GitHubReleaseParser.ParseList(body, includePrerelease: true))
            ?? throw new Mods.StoreException("No AMD NR release with a checksum was found, so nothing was downloaded.");
        var (release, asset) = pick;
        var dir = Path.Combine(AmdNrDir, EmuReleaseCompare.FolderName(release.Tag));
        if (Directory.Exists(dir) && AmdNrRelease.MissingFiles(f => File.Exists(Path.Combine(dir, f))).Count == 0) return dir;

        Directory.CreateDirectory(AmdNrDir);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AmdNrDir))!).AvailableFreeSpace;
        if (free < AmdNrRelease.RequiredFreeBytes(asset))
            throw new Mods.StoreException($"AMD NR needs about {AmdNrRelease.RequiredFreeBytes(asset) / 1048576} MB of free space; {free / 1048576} MB is free.");

        var temp = Path.Combine(Path.GetTempPath(), "fitz-amdnr-" + Guid.NewGuid().ToString("N") + ".zip");
        var partial = dir + ".partial";
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            using (var resp = await s.StoreHttp.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new Mods.StoreException($"The AMD NR download failed ({(int)resp.StatusCode}).");
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(temp);
                var buf = new byte[1 << 16]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    progress?.Report($"Downloading AMD NR {release.Tag}… {done / 1048576} of {asset.Size / 1048576} MB");
                }
            }
            if (new FileInfo(temp).Length != asset.Size) throw new Mods.StoreException("The AMD NR download is the wrong size. Nothing was installed.");
            await using (var fs = File.OpenRead(temp))
            {
                var got = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (got != asset.Sha256) throw new Mods.StoreException("The AMD NR download's checksum doesn't match what GitHub published. Nothing was installed.");
            }
            progress?.Report("Unpacking AMD NR…");
            await Task.Run(() => EmuArchive.ExtractAll(temp, partial), ct).ConfigureAwait(false);
            var missing = AmdNrRelease.MissingFiles(f => File.Exists(Path.Combine(partial, f)));
            if (missing.Count > 0) throw new Mods.StoreException($"The AMD NR download is missing {string.Join(", ", missing)}. Nothing was installed.");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(partial, dir);
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            throw new Mods.StoreException($"AMD NR couldn't be prepared: {e.Message}");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch (IOException) { }
        }
    }
}
