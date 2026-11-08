using System.Diagnostics;
using System.Net;
using System.Net.Http;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Trainers;

namespace FitzLauncher.App.Services.Mods;

/// <summary>
/// FLiNG trainers: search flingtrainer.com, read a trainer's page (downloads, supported versions, cheats),
/// download and unpack it into the launcher's trainers folder, and start/stop it with the game. Only
/// flingtrainer.com links are followed. Antivirus apps often flag trainers; the launcher never touches
/// antivirus settings and explains what happened instead.
/// </summary>
public sealed class TrainerService(AppServices s)
{
    private readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FitzLauncher");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");
        return h;
    }

    public string FolderFor(Game g) => Path.Combine(s.Paths.Root, "trainers", g.Id.ToString("N"));

    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        if (!FlingParser.IsTrustedUrl(url)) throw new StoreException("Only flingtrainer.com pages are opened.");
        using var res = await _http.GetAsync(url, ct);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)429)
            throw new StoreException("FLiNG's site blocked the automatic request. Use Open page, download the trainer there, then Use a downloaded trainer.");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task<List<TrainerSearchResult>> SearchAsync(string gameName, CancellationToken ct = default) =>
        FlingParser.ParseSearch(await GetHtmlAsync(FlingParser.SearchUrl + Uri.EscapeDataString(gameName), ct));

    public async Task<TrainerPage> PageAsync(string url, CancellationToken ct = default) => FlingParser.ParsePage(await GetHtmlAsync(url, ct), url);

    /// <summary>Downloads a trainer archive (or exe), unpacks it and returns the trainer exe path.</summary>
    public async Task<string> DownloadAsync(Game g, TrainerDownload d, CancellationToken ct = default)
    {
        if (!FlingParser.IsTrustedUrl(d.Url)) throw new StoreException("Only flingtrainer.com downloads are used.");
        using var res = await _http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new StoreException($"FLiNG download failed ({(int)res.StatusCode}). Use Open page and download it yourself.");
        var name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName ?? Path.GetFileName(new Uri(d.Url).AbsolutePath);
        name = ModInstallPlanner.SafeName(name.Trim('"'));
        if (!Path.HasExtension(name)) name += ".zip";
        if (res.Content.Headers.ContentType?.MediaType == "text/html") throw new StoreException("That link opened a web page instead of a file. Use Open page and download it from there.");
        var dir = FolderFor(g);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        try
        {
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(file)) await src.CopyToAsync(dst, ct);
            return UseFile(g, file);
        }
        catch (IOException e) when (IsBlockedByAntivirus(e))
        {
            throw new StoreException(AntivirusMessage);
        }
    }

    public const string AntivirusMessage =
        "Windows Security (or your antivirus) removed the trainer as soon as it was saved. Trainers edit game memory, so antivirus apps often flag them. " +
        "If you trust it, allow it in Windows Security → Protection history, or add the launcher's trainers folder as an exclusion, then try again.";

    private static bool IsBlockedByAntivirus(IOException e) => (e.HResult & 0xFFFF) is 225 or 226; // ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED

    /// <summary>A trainer you downloaded yourself (exe or archive): unpacks archives and returns the exe.</summary>
    public string UseFile(Game g, string path)
    {
        var dir = FolderFor(g);
        Directory.CreateDirectory(dir);
        try
        {
            if (ModArchive.IsArchive(path))
            {
                var entries = ModArchive.ListEntries(path);
                var plan = entries.Where(ModInstallPlanner.IsSafeRelative).Select(e => new PlannedFile(e, e)).ToList();
                var unpackTo = Path.Combine(dir, Path.GetFileNameWithoutExtension(path));
                ModArchive.Extract(path, plan, unpackTo);
                var exe = Directory.EnumerateFiles(unpackTo, "*.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => Path.GetFileName(f).Contains("Trainer", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                return exe ?? throw new StoreException("No .exe in that archive. If Windows Security removed it, see Protection history.");
            }
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new StoreException("Pick the trainer's .exe or the archive it came in.");
            if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                var copy = Path.Combine(dir, Path.GetFileName(path));
                File.Copy(path, copy, true);
                return copy;
            }
            return path;
        }
        catch (IOException e) when (IsBlockedByAntivirus(e)) { throw new StoreException(AntivirusMessage); }
        catch (SharpCompress.Common.CryptographicException) { throw new StoreException("This archive is password protected. FLiNG's page lists the password; unpack it yourself and pick the .exe."); }
    }

    // ------------------------------------------------------------------ with the game

    /// <summary>Starts the trainer after the configured delay; returns the process so it can be closed with the game.</summary>
    public async Task<Process?> StartAsync(TrainerConfig t, Action<string> status, CancellationToken ct)
    {
        if (!File.Exists(t.ExePath)) { status($"Trainer not found: {t.ExePath}"); return null; }
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(t.DelaySeconds, 0, 300)), ct); }
        catch (TaskCanceledException) { return null; }
        try
        {
            var p = Process.Start(new ProcessStartInfo(t.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(t.ExePath)! });
            status($"Trainer started: {t.Name}");
            return p;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            status(e.NativeErrorCode == 225 ? AntivirusMessage : $"Couldn't start the trainer: {e.Message}");
            return null;
        }
    }

    public static void Stop(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { p.Dispose(); }
    }

    /// <summary>"Num 1 Infinite health · Num 3 Infinite stamina" for the in-game note.</summary>
    public static string FavouritesLine(TrainerConfig t) =>
        string.Join(" · ", (t.Cheats.Any(c => c.Favourite) ? t.Cheats.Where(c => c.Favourite) : t.Cheats.Take(3)).Select(c => $"{c.Hotkey} {c.Name}"));
}
