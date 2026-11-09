using System.Diagnostics;
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
