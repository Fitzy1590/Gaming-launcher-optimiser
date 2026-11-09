using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.App.Services;

/// <summary>
/// Downloads emulators from their own GitHub releases into <c>&lt;EmuRoot&gt;\&lt;id&gt;\versions\&lt;tag&gt;</c>, keeps settings and saves in a shared
/// <c>data</c> folder outside the versions, and can roll back. Updating never overwrites the running copy and never touches the user's own installs.
/// </summary>
public sealed class EmulatorInstaller(AppServices s, EmulationService emu)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15), DefaultRequestHeaders = { { "User-Agent", "FitzLauncher-emulators" } } };

    private string Root => s.Settings.Emulation.EmuRoot.Length > 0 ? s.Settings.Emulation.EmuRoot : Path.Combine(s.Paths.Root, "emulators");
    private string Dir(string id) => Path.Combine(Root, id);
    private string CurrentFile(string id) => Path.Combine(Dir(id), "current.json");
    private string CacheFile(string id) => Path.Combine(s.Paths.Root, "emu-cache", $"releases-{id}.json");

    public InstalledEmulator? Installed(string id) => Json.Load<InstalledEmulator>(CurrentFile(id));

    private HttpRequestMessage Get(string url, string? etag = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (s.Updates.Token is { } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return req;
    }

    private sealed class CachedList { public string? ETag { get; set; } public string Body { get; set; } = ""; public DateTimeOffset CheckedAt { get; set; } }

    /// <summary>The newest release for the emulator's channel (stable skips prereleases). Uses the saved copy while GitHub says nothing changed.</summary>
    public async Task<(EmuRelease? Release, string? Error)> LatestAsync(EmulatorSource src, CancellationToken ct = default)
    {
        if (src.Type != EmulatorSourceType.GitHub || string.IsNullOrWhiteSpace(src.Repo)) return (null, "This emulator isn't downloaded from GitHub.");
        var nightly = s.Settings.Emulation.Channels.TryGetValue(src.Id, out var ch) && ch.Equals("nightly", StringComparison.OrdinalIgnoreCase);
        var cache = Json.Load<CachedList>(CacheFile(src.Id));
        try
        {
            using var resp = await _http.SendAsync(Get($"https://api.github.com/repos/{src.Repo}/releases?per_page=15", cache?.ETag), ct).ConfigureAwait(false);
            string body;
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && cache is not null) body = cache.Body;
            else if (!resp.IsSuccessStatusCode)
                return (null, resp.StatusCode == System.Net.HttpStatusCode.Forbidden ? "GitHub is limiting requests for now. Add a GitHub token in Settings or try again later." : $"GitHub answered {(int)resp.StatusCode}.");
            else
            {
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Json.Save(CacheFile(src.Id), new CachedList { ETag = resp.Headers.ETag?.Tag, Body = body, CheckedAt = DateTimeOffset.Now });
            }
            var release = GitHubReleaseParser.ParseList(body, nightly).FirstOrDefault();
            return release is null ? (null, "No releases found.") : (release, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cache is not null && GitHubReleaseParser.ParseList(cache.Body, nightly).FirstOrDefault() is { } old) return (old, "Offline: showing the last check.");
            return (null, "Couldn't reach GitHub.");
        }
    }

    /// <summary>For the Test sources button: what the updater would download for each GitHub emulator, so wrong patterns show up.</summary>
    public async Task<List<string>> TestSourcesAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var src in emu.Sources.Where(x => x.Type == EmulatorSourceType.GitHub))
        {
            var (rel, err) = await LatestAsync(src, ct).ConfigureAwait(false);
            if (rel is null) { lines.Add($"{src.Name}: {err}"); continue; }
            var pick = AssetPicker.Pick(rel.Assets, src.AssetPattern);
            lines.Add(pick is null
                ? $"{src.Name}: release {rel.Tag} found but no Windows download matched. Assets: {string.Join(", ", rel.Assets.Select(a => a.Name).Take(6))}"
                : $"{src.Name}: {rel.Tag} → {pick.Name}{(pick.Sha256 is null ? " (no checksum published)" : " (checksum verified on download)")}");
        }
        return lines;
    }

    public sealed record Progress(string Text, double? Fraction);

    public async Task<string> InstallAsync(EmulatorSource src, EmuRelease release, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        var asset = AssetPicker.Pick(release.Assets, src.AssetPattern);
        if (asset is null) return $"No Windows download matched in {release.Tag}. Use Test sources to see what the release holds.";
        if (!EmuReleaseCompare.IsAllowedHost(asset.Url)) return "The download isn't hosted on GitHub, so Fitz won't fetch it.";

        var folder = EmuReleaseCompare.FolderName(release.Tag);
        var versions = Path.Combine(Dir(src.Id), "versions");
        var target = Path.Combine(versions, folder);
        var partial = target + ".partial";
        var temp = Path.Combine(Path.GetTempPath(), "fitz-" + Guid.NewGuid().ToString("N") + Path.GetExtension(asset.Name));
        try
        {
            Directory.CreateDirectory(versions);
            if (Directory.Exists(partial)) Directory.Delete(partial, true);

            progress?.Report(new($"Downloading {asset.Name}…", 0));
            using (var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return $"Download failed ({(int)resp.StatusCode}).";
                var total = resp.Content.Headers.ContentLength ?? asset.Size;
                await using var src2 = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(temp);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src2.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(new($"Downloading {asset.Name}… {done / 1048576} of {total / 1048576} MB", done / (double)total));
                }
            }

            var size = new FileInfo(temp).Length;
            if (asset.Size > 0 && size != asset.Size) return $"The download is the wrong size ({size} bytes, expected {asset.Size}). Nothing was installed.";
            if (asset.Sha256 is { } want)
            {
                await using var fs = File.OpenRead(temp);
                var got = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (got != want) return "The download's checksum doesn't match what GitHub published. Nothing was installed.";
            }

            progress?.Report(new("Unpacking…", null));
            await Task.Run(() => EmuArchive.ExtractAll(temp, partial), ct).ConfigureAwait(false);
            if (EmuArchive.FindExe(partial, src.ExeNames) is null) { Directory.Delete(partial, true); return $"The download doesn't contain {string.Join(" or ", src.ExeNames)}. Nothing was installed."; }

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(partial, target);
            var note = LinkData(src, target);
            var exe = EmuArchive.FindExe(target, src.ExeNames)!;

            var prev = Installed(src.Id);
            var rec = new InstalledEmulator { Tag = release.Tag, Name = release.Name, PublishedAt = release.PublishedAt, Exe = exe, Previous = [.. (prev?.Previous ?? []).Prepend(prev?.Tag ?? "").Where(t => t.Length > 0 && t != release.Tag).Distinct().Take(2)] };
            Json.Save(CurrentFile(src.Id), rec);
            emu.SetPath(src.Id, exe);
            Prune(src.Id, rec);
            return $"{src.Name} {release.Tag} is installed and in use. Your settings and saves are kept in a shared folder, so updates and roll-backs don't lose them.{note}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or TaskCanceledException)
        {
            s.Log.Write($"Emulator install failed ({src.Id}): {e.Message}");
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch (IOException) { }
            return $"Install failed: {e.Message}";
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    /// <summary>Points the version's data folders at the shared <c>data</c> folder (a junction needs no admin rights) and adds the portable marker file.</summary>
    private string LinkData(EmulatorSource src, string versionDir)
    {
        var shared = Path.Combine(Dir(src.Id), "data");
        var failed = new List<string>();
        foreach (var name in src.DataDirs)
        {
            try
            {
                var inVersion = Path.Combine(versionDir, name);
                var inShared = Path.Combine(shared, name);
                Directory.CreateDirectory(inShared);
                if (Directory.Exists(inVersion))
                {
                    // Defaults that shipped in the download only seed an empty shared folder.
                    if (!Directory.EnumerateFileSystemEntries(inShared).Any()) CopyDir(inVersion, inShared);
                    Directory.Delete(inVersion, true);
                }
                if (!MakeJunction(inVersion, inShared)) failed.Add(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add(name); }
        }
        if (src.PortableMarker is { } marker && Path.HasExtension(marker))
        {
            try { var m = Path.Combine(versionDir, marker); if (!File.Exists(m)) File.WriteAllText(m, ""); }
            catch (IOException) { }
        }
        return failed.Count > 0 ? $" Couldn't share {string.Join(", ", failed)} between versions, so those stay inside this version." : "";
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private void Prune(string id, InstalledEmulator keep)
    {
        var versions = Path.Combine(Dir(id), "versions");
        var keepNames = new[] { keep.Tag }.Concat(keep.Previous).Select(EmuReleaseCompare.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.GetDirectories(versions))
        {
            if (keepNames.Contains(Path.GetFileName(d))) continue;
            try { Directory.Delete(d, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Goes back to the previous installed version (data is shared, so nothing is lost).</summary>
    public string Rollback(EmulatorSource src)
    {
        var cur = Installed(src.Id);
        if (cur is null || cur.Previous.Count == 0) return "There's no earlier version to go back to.";
        var tag = cur.Previous[0];
        var dir = Path.Combine(Dir(src.Id), "versions", EmuReleaseCompare.FolderName(tag));
        var exe = Directory.Exists(dir) ? EmuArchive.FindExe(dir, src.ExeNames) : null;
        if (exe is null) return $"Version {tag} is no longer on disk.";
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0) return $"Close {src.Name} first.";
        var rec = new InstalledEmulator { Tag = tag, Name = tag, PublishedAt = DateTimeOffset.MinValue, Exe = exe, Previous = [.. cur.Previous.Skip(1).Prepend(cur.Tag).Distinct()] };
        Json.Save(CurrentFile(src.Id), rec);
        emu.SetPath(src.Id, exe);
        return $"Went back to {src.Name} {tag}.";
    }
}
