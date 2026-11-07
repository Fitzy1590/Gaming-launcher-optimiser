using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FitzLauncher.Core.Updates;

namespace FitzLauncher.App.Services;

public sealed record UpdateCheck(bool Available, ReleaseInfo? Release, string Message);

/// <summary>
/// Checks GitHub Releases for a newer build and installs it in place: download the zip, unpack it next
/// to the current install, then a small script waits for the launcher to close, copies the files over
/// and starts the new version. Your library, settings and backups live in %LOCALAPPDATA% and are untouched.
/// </summary>
public sealed class UpdateService(HttpClient http, AppServices services)
{
    public static Version CurrentVersion =>
        ReleaseInfo.Normalise(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    // ---------------------------------------------------------------- token (only while the repo is private)

    public string? Token
    {
        get
        {
            var p = services.Settings.GitHubTokenProtected;
            if (string.IsNullOrEmpty(p)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
        set
        {
            services.Settings.GitHubTokenProtected = string.IsNullOrWhiteSpace(value) ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
            services.SaveSettings();
        }
    }

    private HttpRequestMessage Request(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (Token is { } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return req;
    }

    // ---------------------------------------------------------------- check

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await http.SendAsync(Request(ReleaseInfo.LatestUrl, "application/vnd.github+json"), ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new(false, null, Token is null
                    ? "Couldn't see any releases. The GitHub repository is private: make it public, or add a read-only token below."
                    : "No releases found yet (or the token can't read this repository).");
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, null, "GitHub refused the request: the token is invalid/expired, or the rate limit was hit. Try again later.");
            res.EnsureSuccessStatusCode();
            var release = ReleaseInfo.Parse(await res.Content.ReadAsStringAsync(ct));
            if (release is null) return new(false, null, "The latest release has no version number.");
            return release.IsNewerThan(CurrentVersion)
                ? new(true, release, $"Version {release.Version} is available (you have {CurrentVersion}).")
                : new(false, release, $"You're up to date ({CurrentVersion}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(false, null, "Couldn't check for updates: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- install

    public async Task<string> InstallAsync(ReleaseInfo release, IProgress<string> progress, CancellationToken ct = default)
    {
        if (release.AssetApiUrl is null && release.AssetDownloadUrl is null) return "That release has no download attached.";
        var work = Path.Combine(Path.GetTempPath(), "FitzLauncherUpdate", release.Version.ToString());
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, ReleaseInfo.AssetName);

        progress.Report("Downloading…");
        // The API asset URL works for private repos (with the token); GitHub redirects to the file itself.
        using (var req = release.AssetApiUrl is not null
                   ? Request(release.AssetApiUrl, "application/octet-stream")
                   : Request(release.AssetDownloadUrl!, "application/octet-stream"))
        using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0) progress.Report($"Downloading… {done * 100 / total.Value}%");
            }
        }

        progress.Report("Unpacking…");
        var staged = Path.Combine(work, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged), ct);
        if (!File.Exists(Path.Combine(staged, "FitzLauncher.exe"))) return "The download doesn't contain FitzLauncher.exe; update cancelled.";

        var installDir = AppContext.BaseDirectory.TrimEnd('\\');
        var script = Path.Combine(work, "apply-update.cmd");
        var pid = Environment.ProcessId;
        await File.WriteAllTextAsync(script, $"""
            @echo off
            rem Fitz Launcher updater: wait for the launcher to close, copy the new files, start it again.
            :wait
            tasklist /FI "PID eq {pid}" | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            robocopy "{staged}" "{installDir}" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            start "" "{Path.Combine(installDir, "FitzLauncher.exe")}"
            """, ct);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = work });
        return "";
    }
}
