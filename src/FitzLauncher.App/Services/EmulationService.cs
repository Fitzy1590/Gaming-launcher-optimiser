using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.App.Services;

/// <summary>Emulation Hub work that touches the disk: finding emulators, writing quality settings before a game starts, learning from sessions.</summary>
public sealed class EmulationService(AppServices s)
{
    private EmulatorInstaller? _installer;
    public EmulatorInstaller Installer => _installer ??= new EmulatorInstaller(s, this);

    private string SourcesFile => Path.Combine(s.Paths.Root, "emulator-sources.json");
    private string RestoreDir(string id) => Path.Combine(s.Paths.Root, "emu-restore", id);
    private static readonly UTF8Encoding Utf8 = new(false);

    public IReadOnlyList<EmulatorSource> Sources
    {
        get
        {
            string? json = null;
            try { if (File.Exists(SourcesFile)) json = File.ReadAllText(SourcesFile); } catch (IOException) { }
            var (list, error) = EmulatorSources.Merge(json);
            if (error is not null) s.Log.Write(error);
            return list;
        }
    }

    public EmulatorSource? Source(string? id) => id is null ? null : Sources.FirstOrDefault(x => x.Id == id);

    public string? ExePath(string id) => s.Settings.Emulation.EmulatorPaths.TryGetValue(id, out var p) && File.Exists(p) ? p : null;

    public HashSet<string> InstalledIds() => [.. Sources.Where(x => ExePath(x.Id) is not null).Select(x => x.Id)];

    public GpuTier Tier() => s.Hardware?.PrimaryGpu is { } g ? GpuTierGuess.Classify(g.Name, g.IsDiscrete) : GpuTier.Integrated;

    public int Ceiling(Game game) => game.Platform is { } p ? EmuTuner.Ceiling(Tier(), p) : 1;

    public int TargetFor(Game game) => game.FpsTarget ?? (game.Platform is { } p ? Platforms.DefaultTarget(p) : 60);

    // ------------------------------------------------------------------ discovery

    /// <returns>How many emulators were newly found.</returns>
    public async Task<int> FindEmulatorsAsync(CancellationToken ct = default)
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            foreach (var n in new[] { "Emulators", "Emulation", "Games", @"Games\Emulators", "Emulator" }) roots.Add(Path.Combine(d.Name, n));
        roots.AddRange(s.Settings.Emulation.ScanFolders);
        if (s.Settings.Emulation.EmuRoot.Length > 0) roots.Add(s.Settings.Emulation.EmuRoot);

        var sources = Sources;
        var found = await Task.Run(() => EmulatorDiscovery.Scan(roots, sources), ct).ConfigureAwait(false);
        var added = 0;
        foreach (var f in found)
            if (ExePath(f.Id) is null) { s.Settings.Emulation.EmulatorPaths[f.Id] = f.Path; added++; }
        if (added > 0) s.SaveSettings();
        return added;
    }

    public void SetPath(string id, string path)
    {
        s.Settings.Emulation.EmulatorPaths[id] = path;
        s.SaveSettings();
    }

    // ------------------------------------------------------------------ quality settings

    private static bool IsRunning(string exePath) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)).Length > 0;

    public sealed record ApplyResult(List<string> Applied, List<string> Skipped, string? Problem);

    /// <summary>Writes the quality level's settings into the emulator's own settings file (only keys that already exist).</summary>
    public ApplyResult ApplyLevel(Game game, int level)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var src = Source(game.EmulatorId);
        if (src is null) return new(applied, skipped, "This game isn't linked to an emulator.");
        var map = EmuTuneMaps.For(src.Id);
        if (map is null) return new(applied, skipped, $"Fitz can't change {src.Name}'s settings yet. Set quality in the emulator itself.");
        var exe = ExePath(src.Id);
        if (exe is null) return new(applied, skipped, $"{src.Name} isn't set up. Use Find emulators or Browse.");
        if (IsRunning(exe)) return new(applied, skipped, $"Close {src.Name} first: it rewrites its settings when it closes.");

        var dir = Path.GetDirectoryName(exe)!;
        var q = QualityLevel.For(level);
        foreach (var (file, spec) in map.Files)
        {
            var path = EmuTuneMaps.Locate(spec, dir, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), File.Exists);
            if (path is null) { skipped.Add($"{src.Name} hasn't created its settings file yet. Open it once, close it, then Optimise again."); continue; }
            try
            {
                var text = File.ReadAllText(path);
                var result = ConfigPatcher.Patch(spec.Format, text, EmuTuneMaps.EditsFor(map, file, q));
                skipped.AddRange(result.Skipped);
                if (!result.Changed) { applied.AddRange(result.Applied); continue; }
                Directory.CreateDirectory(RestoreDir(src.Id));
                var backup = Path.Combine(RestoreDir(src.Id), Path.GetFileName(path) + ".original");
                if (!File.Exists(backup)) File.Copy(path, backup);                  // the file as it was before Fitz first touched it
                var restoreMap = Path.Combine(RestoreDir(src.Id), "files.json");
                var files = Json.Load<Dictionary<string, string>>(restoreMap) ?? [];
                files[backup] = path;
                Json.Save(restoreMap, files);
                var tmp = path + ".fitz-tmp";
                File.WriteAllText(tmp, result.Text, Utf8);
                File.Move(tmp, path, overwrite: true);
                applied.AddRange(result.Applied);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                s.Log.Write($"Emulator settings write failed ({path}): {e.Message}");
                skipped.Add($"Couldn't write {Path.GetFileName(path)}: {e.Message}");
            }
        }
        return new(applied, skipped, null);
    }

    /// <summary>The "1-click optimise": pick a starting quality for this PC and the console, set the FPS target, switch on automatic adjustment.</summary>
    public string Optimise(Game game)
    {
        if (game.Platform is not { } platform) return "This isn't an emulator game.";
        var ceiling = Ceiling(game);
        var level = game.EmuTuning.Level > 0 ? Math.Min(game.EmuTuning.Level, ceiling) : Math.Min(EmuTuner.StartLevel(Tier(), platform), ceiling);
        game.EmuTuning.Level = level;
        game.EmuAutoTune = true;
        game.FpsTarget ??= Platforms.DefaultTarget(platform);
        var r = ApplyLevel(game, level);
        game.EmuTuning.AppliedAt = r.Problem is null ? DateTimeOffset.Now : game.EmuTuning.AppliedAt;
        s.SaveLibrary();

        var sb = new StringBuilder();
        sb.Append($"Quality level {level} of {QualityLevel.Max}: {QualityLevel.For(level).Describe()}. Target {game.FpsTarget} FPS. ");
        sb.Append("After each session Fitz moves the quality up or down to hold the target. ");
        if (r.Problem is not null) sb.Append(r.Problem);
        else if (r.Applied.Count > 0) sb.Append($"Changed {r.Applied.Count} setting(s).");
        if (r.Skipped.Count > 0) sb.Append($" Not changed: {string.Join("; ", r.Skipped.Take(3))}.");
        return sb.ToString().Trim();
    }

    /// <summary>Right before launch: each game gets its own quality level (the emulator's settings are shared).</summary>
    public string? PrepareLaunch(Game game)
    {
        if (game.Platform is null || game.EmuTuning.Level <= 0) return null;
        var r = ApplyLevel(game, game.EmuTuning.Level);
        s.Log.Write($"[{game.Name}] emulator quality level {game.EmuTuning.Level}: applied {r.Applied.Count}, skipped {r.Skipped.Count}{(r.Problem is null ? "" : $", problem: {r.Problem}")}");
        return r.Problem;
    }

    /// <summary>Puts the emulator's settings file back as it was before Fitz first changed it.</summary>
    public string Revert(Game game)
    {
        var id = game.EmulatorId;
        if (id is null) return "Nothing to revert.";
        var exe = ExePath(id);
        if (exe is not null && IsRunning(exe)) return "Close the emulator first.";
        var files = Json.Load<Dictionary<string, string>>(Path.Combine(RestoreDir(id), "files.json"));
        if (files is null || files.Count == 0) return "Fitz hasn't changed this emulator's settings.";
        var done = 0;
        foreach (var (backup, original) in files)
        {
            try { if (File.Exists(backup)) { File.Copy(backup, original, overwrite: true); done++; } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s.Log.Write($"Revert failed ({original}): {e.Message}"); }
        }
        game.EmuAutoTune = false;
        game.EmuTuning = new EmuTuningState();
        s.SaveLibrary();
        return done > 0 ? $"Restored {done} settings file(s) to how they were before Fitz changed them (this affects every game on that emulator)." : "Couldn't restore the settings file.";
    }

    /// <summary>Update and DLC files for a Switch game found in the user's own folders (names only; nothing is read from the files).</summary>
    public string? TitleInfo(Game game)
    {
        if (game.Platform != EmuPlatform.Switch || game.RomPath is not { } rom || TitleRelations.Parse(rom) is not { } self) return null;
        game.TitleId = self.TitleId;
        try
        {
            var roots = s.Settings.Emulation.RomFolders.GetValueOrDefault(nameof(EmuPlatform.Switch)) ?? [];
            var folder = roots.FirstOrDefault(r => rom.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? Path.GetDirectoryName(rom);
            if (folder is null || !Directory.Exists(folder)) return null;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(20000);
            return TitleRelations.Summarise(rom, files).Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ game IDs, descriptions, mods

    /// <summary>Reads the game's own ID (serial / title ID) from the file's header, once.</summary>
    public void Identify(Game game)
    {
        if (game.Platform is not { } p || game.RomPath is not { } rom || !string.IsNullOrEmpty(game.TitleId)) return;
        if (p == EmuPlatform.Switch) { game.TitleId = TitleRelations.Parse(rom)?.TitleId; return; }
        if (RomHeaders.Identify(p, rom) is { } id) game.TitleId = id.TitleId;
    }

    public static string? Protect(string? plain) => string.IsNullOrWhiteSpace(plain) ? null
        : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain.Trim()), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try { return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    public bool HasIgdbKeys => !string.IsNullOrWhiteSpace(s.Settings.Emulation.IgdbClientId) && Unprotect(s.Settings.Emulation.IgdbClientSecretProtected) is not null;

    private string? _igdbToken;
    private DateTimeOffset _igdbTokenUntil;
    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <returns>A message when it couldn't (no keys, offline, no match), null when the description was saved.</returns>
    public async Task<string?> FetchMetaAsync(Game game, CancellationToken ct = default)
    {
        if (game.Platform is not { } platform) return null;
        var id = s.Settings.Emulation.IgdbClientId;
        var secret = Unprotect(s.Settings.Emulation.IgdbClientSecretProtected);
        if (string.IsNullOrWhiteSpace(id) || secret is null) return "Add your IGDB keys on the Emulators tab to get descriptions.";
        try
        {
            if (_igdbToken is null || DateTimeOffset.Now >= _igdbTokenUntil)
            {
                using var tr = await Web.PostAsync(Igdb.TokenUrl + Igdb.TokenQuery(id, secret), null, ct).ConfigureAwait(false);
                if (!tr.IsSuccessStatusCode) return "IGDB didn't accept the keys. Check the client ID and secret.";
                using var doc = System.Text.Json.JsonDocument.Parse(await tr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                _igdbToken = doc.RootElement.GetProperty("access_token").GetString();
                _igdbTokenUntil = DateTimeOffset.Now.AddSeconds(Math.Max(60, doc.RootElement.GetProperty("expires_in").GetInt32() - 300));
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, Igdb.GamesUrl) { Content = new StringContent(Igdb.SearchBody(game.Name, Platforms.Get(platform).IgdbPlatformId), System.Text.Encoding.UTF8, "text/plain") };
            req.Headers.Add("Client-ID", id);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _igdbToken);
            using var resp = await Web.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return $"IGDB answered {(int)resp.StatusCode}.";
            var meta = Igdb.BestMatch(Igdb.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), game.Name);
            if (meta is null) return "IGDB has no clear match for this title.";
            game.Meta = meta;
            s.SaveLibrary();
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            s.Log.Write($"IGDB lookup failed for {game.Name}: {e.Message}");
            return "Couldn't reach IGDB.";
        }
    }

    /// <summary>The emulator's user folder (where mods, textures and patches live), found from its settings file; null until the emulator has run once.</summary>
    public string? UserDir(string emulatorId)
    {
        var map = EmuTuneMaps.For(emulatorId);
        var exe = ExePath(emulatorId);
        if (map is null || exe is null) return null;
        var spec = map.Files.Values.First();
        var cfg = EmuTuneMaps.Locate(spec, Path.GetDirectoryName(exe)!, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), File.Exists);
        if (cfg is null) return null;
        var dir = Path.GetDirectoryName(cfg)!;
        return emulatorId switch
        {
            "ppsspp" => Directory.GetParent(Directory.GetParent(dir)!.FullName)?.FullName,
            "xenia-canary" or "ryubing" => dir,
            _ => Directory.GetParent(dir)?.FullName,
        };
    }

    public string AddMod(Game game, string source, EmuModKind kind, string name)
    {
        if (game.EmulatorId is not { } emu) return "This game isn't linked to an emulator.";
        Identify(game);
        var rel = ModDestinations.For(emu, kind, game.TitleId, name, out var why);
        if (rel is null) return why;
        var user = UserDir(emu);
        if (user is null) return $"Open {Source(emu)?.Name} once and close it, so it creates its folders, then add the mod again.";
        var mod = new EmuModEntry { Name = name, Kind = kind };
        mod.LibraryDir = Path.Combine(s.Paths.Root, "emu-mods", emu, game.Id.ToString("N"), mod.Id);
        try { EmuModStore.Import(source, mod.LibraryDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return $"Couldn't unpack it: {e.Message}"; }
        game.EmuMods.Add(mod);
        var r = EmuModStore.Enable(mod, Path.Combine(user, rel));
        if (!r.Ok) { game.EmuMods.Remove(mod); try { Directory.Delete(mod.LibraryDir, true); } catch (IOException) { } }
        s.SaveLibrary();
        return r.Ok ? $"{name}: {r.Message}" : r.Message + (r.Conflicts.Count > 0 ? $" ({string.Join(", ", r.Conflicts.Take(3))})" : "");
    }

    public string ToggleMod(Game game, EmuModEntry mod)
    {
        if (mod.Enabled) { var off = EmuModStore.Disable(mod); s.SaveLibrary(); return off.Message; }
        if (game.EmulatorId is not { } emu) return "This game isn't linked to an emulator.";
        var rel = ModDestinations.For(emu, mod.Kind, game.TitleId, mod.Name, out var why);
        var user = UserDir(emu);
        if (rel is null) return why;
        if (user is null) return "Open the emulator once so it creates its folders.";
        var r = EmuModStore.Enable(mod, Path.Combine(user, rel));
        s.SaveLibrary();
        return r.Message;
    }

    public string RemoveMod(Game game, EmuModEntry mod)
    {
        if (mod.Enabled) EmuModStore.Disable(mod);
        try { if (Directory.Exists(mod.LibraryDir)) Directory.Delete(mod.LibraryDir, true); } catch (IOException) { }
        game.EmuMods.Remove(mod);
        s.SaveLibrary();
        return $"{mod.Name} removed.";
    }

    // ------------------------------------------------------------------ learning

    /// <summary>After a session: record how the game ran on this emulator and, with auto-tune on, move the quality for next time.</summary>
    public string? AfterSession(Game game, SessionSummary summary)
    {
        if (game.Platform is null || game.EmulatorId is not { } id) return null;
        var level = game.EmuTuning.Level > 0 ? game.EmuTuning.Level : (int?)null;
        var recorded = EmulatorAdvisor.Record(game.EmuPerf, id, summary.Duration.TotalSeconds, summary.HasFpsData, summary.AverageFps, summary.OnePercentLowFps, summary.TimeAtTarget, level, summary.End);
        if (!recorded || !game.EmuAutoTune || game.EmuTuning.Level <= 0) return null;
        var r = EmuTuner.Adjust(game.EmuTuning, Ceiling(game), summary.AverageFps, summary.OnePercentLowFps, summary.AvgGpuLoad, TargetFor(game), summary.Duration);
        game.EmuTuning.LastNote = r.Note;
        return r.Note;
    }

    public EmulatorSuggestion? Suggestion(Game game)
    {
        if (game.Platform is not { } p || game.EmulatorId is not { } id) return null;
        return EmulatorAdvisor.Suggest(p, id, TargetFor(game), game.EmuPerf, Sources, InstalledIds(), game.EmuTuning.Level == 0 ? 99 : game.EmuTuning.Level);
    }

    /// <summary>Moves a game to another emulator. Its measured results are kept so the two can be compared.</summary>
    public string? SwitchEmulator(Game game, string toId)
    {
        var src = Source(toId);
        if (src is null) return "Unknown emulator.";
        if (ExePath(toId) is not { } exe) return $"{src.Name} isn't set up. Use Find emulators or Browse first.";
        EmulationLibrary.Point(game, src, exe);
        game.EmuTuning = new EmuTuningState();
        s.SaveLibrary();
        return null;
    }
}
