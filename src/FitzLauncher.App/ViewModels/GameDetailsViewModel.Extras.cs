using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Runtimes;
using FitzLauncher.Core.Saves;
using FitzLauncher.Core.Stats;
using FitzLauncher.Core.Storage;
using FitzLauncher.Windows;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

public sealed partial class ChoiceOption<T>(T value, string label, string? hint = null) : ObservableObject
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    public string? Hint { get; } = hint;
    [ObservableProperty] private bool _isSelected;
}

public sealed record AutoTuneRow(string Title, string Result, string Outcome, bool Good);
public sealed record BenchmarkRow(string When, string Avg, string Low, string PointLow, string AtTarget, string Delta, bool Better, string Label);
public sealed record BackupRow(string When, string Size, string Path);
public sealed record DisplayProfileRow(string Key, string Label, string Detail);

public sealed partial class RuntimeRow(RedistInfo info) : ObservableObject
{
    public RedistInfo Info { get; } = info;
    public string Name => Info.DisplayName;
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private string _state = "";
}

/// <summary>Organisation/progress, performance automation, save backups and runtimes.</summary>
public sealed partial class GameDetailsViewModel
{
    public ObservableCollection<ChoiceOption<PlayStatus>> StatusOptions { get; } = [];
    public ObservableCollection<ChoiceOption<CorePinning>> PinningOptions { get; } = [];
    public ObservableCollection<AutoTuneRow> AutoTuneSteps { get; } = [];
    public ObservableCollection<BenchmarkRow> Benchmarks { get; } = [];
    public ObservableCollection<string> SaveDirs { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];
    public ObservableCollection<RuntimeRow> Runtimes { get; } = [];
    public ObservableCollection<DisplayProfileRow> DisplayProfiles { get; } = [];
    [ObservableProperty] private string _displayText = "";

    [ObservableProperty] private string _pinningText = "";
    [ObservableProperty] private string _autoTuneStatus = "";
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private string _runtimeStatus = "";
    [ObservableProperty] private bool _runtimesChecked;

    private void InitExtras()
    {
        foreach (var (v, l) in new[] { (PlayStatus.Backlog, "Backlog"), (PlayStatus.Playing, "Playing"), (PlayStatus.Beaten, "Beaten"), (PlayStatus.Completed, "100%"), (PlayStatus.Abandoned, "Dropped") })
            StatusOptions.Add(new ChoiceOption<PlayStatus>(v, l));
        PinningOptions.Add(new(CorePinning.Auto, "Auto", "Pins only where it helps: the V-Cache CCD on dual-CCD Ryzen X3D, P-cores on Intel 12th gen+."));
        PinningOptions.Add(new(CorePinning.Off, "Off", "Let Windows schedule freely."));
        PinningOptions.Add(new(CorePinning.PerformanceCores, "P-cores", "Intel hybrid: keep the game off E-cores."));
        PinningOptions.Add(new(CorePinning.VCacheCcd, "V-Cache CCD", "Ryzen 7950X3D/9950X3D: keep the game on the 3D V-Cache die."));
        PinningOptions.Add(new(CorePinning.SingleCcd, "Single CCD", "Two equal CCDs (Ryzen 9, Ryzen AI Max+ 395): keep the game on one 8-core CCD to avoid cross-CCD latency. Benchmark it (Ctrl+Alt+B) with and without; it helps some games and not others."));
        LoadExtras();
    }

    // ------------------------------------------------------------------ report card + crash rollback

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportGrade = "";
    [ObservableProperty] private string _reportScore = "";
    [ObservableProperty] private string _reportSmoothness = "";
    [ObservableProperty] private string _reportTemps = "";
    [ObservableProperty] private string _reportStability = "";
    [ObservableProperty] private string _reportNext = "";
    [ObservableProperty] private bool _hasCrash;
    [ObservableProperty] private string _crashText = "";
    [ObservableProperty] private bool _canRollBack;

    private void LoadReport()
    {
        var card = ReportCards.Compute(Game, _s.Store.LoadSessions(Game.Id), _s.Settings);
        HasReport = card is not null;
        if (card is not null)
        {
            ReportGrade = card.Grade;
            ReportScore = $"{card.Score}/100";
            ReportSmoothness = card.Smoothness;
            ReportTemps = card.Temps;
            ReportStability = card.Stability;
            ReportNext = card.NextStep;
            if (Game.ReportGrade != card.Grade) { Game.ReportGrade = card.Grade; Card.Refresh(); }
        }
        var crash = Game.LastCrash;
        HasCrash = crash is not null && (Game.LastGoodSession is null || crash.When > Game.LastGoodSession);
        CrashText = crash is null ? "" : $"{crash.When.ToLocalTime():d MMM HH:mm}: {CrashAdvisor.Explain(Game, crash)}";
        CanRollBack = HasCrash && CrashAdvisor.Suspects(Game).Count > 0;
    }

    /// <summary>Reverts every change made since the last session that ended cleanly.</summary>
    [RelayCommand]
    private async Task RollBackAsync()
    {
        var suspects = CrashAdvisor.Suspects(Game).Select(c => c.TweakId).Distinct().ToList();
        if (suspects.Count == 0) { Status = "Nothing to roll back: no changes since the last clean session."; return; }
        var errors = new List<string>();
        await Busy("Rolling back recent changes…", async () =>
        {
            await Task.Run(() => { foreach (var id in suspects) errors.AddRange(_s.Tweaks.RevertAll(Game.AppliedChanges, Game, id)); });
            _s.SaveLibrary();
        });
        RebuildPlan();
        LoadReport();
        Card.Refresh();
        Status = errors.Count == 0 ? $"Rolled back {suspects.Count} change(s) made since the last clean session." : "Some changes couldn't be reverted: " + string.Join("; ", errors);
    }

    private void LoadExtras()
    {
        LoadReport();
        LoadTrainer();
        LoadUpdates();
        LoadCooling();
        LoadInsights();
        LoadAmd();
        LoadUpscaling();
        foreach (var o in StatusOptions) o.IsSelected = o.Value == Game.Status;
        foreach (var o in PinningOptions) o.IsSelected = o.Value == Game.CorePinning;
        var plan = CpuAffinityPlanner.Plan(_s.CpuTopology, Game.CorePinning);
        PinningText = plan?.Description ?? (Game.CorePinning == CorePinning.Off ? "All cores (pinning off)." : "All cores: pinning wouldn't help on this CPU.");

        AutoTuneSteps.Clear();
        foreach (var st in Enumerable.Reverse(Game.AutoTune.Steps))
        {
            var result = st.ResultLow is { } rl ? $"1% low {st.BaselineLow:0} → {rl:0}" : $"waiting for next session (baseline 1% low {st.BaselineLow:0})";
            AutoTuneSteps.Add(new AutoTuneRow(st.Title, result, st.Outcome switch
            {
                AutoTuneOutcome.Kept => "Kept", AutoTuneOutcome.Reverted => "Reverted", AutoTuneOutcome.Skipped => "Skipped", _ => "Testing",
            }, st.Outcome == AutoTuneOutcome.Kept));
        }
        AutoTuneStatus = !Game.AutoTune.Enabled ? "Off. Turn it on to let the launcher test graphics changes between sessions."
            : Game.FpsTarget is null ? "Pick an FPS target to use auto-tune."
            : Game.AutoTune.TargetReached ? $"Done: holding {Game.FpsTarget} FPS. It restarts if a session misses the target."
            : Game.AutoTune.Steps.Count == 0 ? $"On. After a session (5 min+) that misses {Game.FpsTarget} FPS, it tries one change and checks the next session."
            : "On: testing one change per session.";

        var benches = _s.Store.LoadBenchmarks(Game.Id).OrderByDescending(b => b.Taken).Take(10).ToList();
        Benchmarks.Clear();
        for (var i = 0; i < benches.Count; i++)
        {
            var b = benches[i];
            var prev = i + 1 < benches.Count ? benches[i + 1] : null;
            var delta = prev is null ? "" : $"{(b.OnePercentLowFps - prev.OnePercentLowFps) / Math.Max(prev.OnePercentLowFps, 1):+0%;-0%;±0%} 1% low";
            Benchmarks.Add(new BenchmarkRow(b.Taken.ToString("d MMM HH:mm"), $"{b.AverageFps:0}", $"{b.OnePercentLowFps:0}", $"{b.PointOnePercentLowFps:0}",
                b.TimeAtTarget is { } t ? $"{t:P0}" : "–", delta, prev is not null && b.OnePercentLowFps >= prev.OnePercentLowFps, b.Label));
        }

        SaveDirs.Clear();
        foreach (var d in Game.SaveDirs) SaveDirs.Add(d);
        LoadDisplay();
        DetectMods();
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------------ organisation & progress

    public string TagsText
    {
        get => string.Join(", ", Game.Tags);
        set
        {
            Game.Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveAndRefresh();
        }
    }

    public string MainStoryHoursText
    {
        get => Game.MainStoryHours?.ToString("0.#", CultureInfo.CurrentCulture) ?? "";
        set
        {
            Game.MainStoryHours = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var h) && h > 0 ? h : null;
            SaveAndRefresh();
        }
    }

    public string CompletionText
    {
        get => Game.CompletionPercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            Game.CompletionPercent = int.TryParse(value.Trim().TrimEnd('%'), out var p) ? Math.Clamp(p, 0, 100) : null;
            SaveAndRefresh();
        }
    }

    public bool Hidden { get => Game.Hidden; set { Game.Hidden = value; SaveAndRefresh(); } }
    public bool Favourite { get => Game.Favourite; set { Game.Favourite = value; SaveAndRefresh(); } }

    public double ProgressValue => Game.ProgressPercent ?? 0;
    public bool HasProgress => Game.ProgressPercent is not null;
    public string ProgressText => Game.ProgressPercent is { } p
        ? $"{p:0}% · {LibraryStats.FormatHours(Game.TotalPlayTime.TotalHours)} played" + (Game.MainStoryHours is { } m && Game.CompletionPercent is null ? $" of ~{m:0} h main story" : "")
        : $"{LibraryStats.FormatHours(Game.TotalPlayTime.TotalHours)} played. Enter the main-story length for a progress bar.";

    [RelayCommand]
    private void SetStatus(ChoiceOption<PlayStatus> option)
    {
        Game.Status = option.Value;
        foreach (var o in StatusOptions) o.IsSelected = o == option;
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        _s.SaveLibrary();
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressText));
        Card.Refresh();
        _refreshMeta();
    }

    // ------------------------------------------------------------------ performance automation

    public bool ThermalGuardOn { get => Game.ThermalGuard; set { Game.ThermalGuard = value; _s.SaveLibrary(); } }
    public bool PauseOnSleepOn { get => Game.PauseOnSleep; set { Game.PauseOnSleep = value; _s.SaveLibrary(); } }
    public bool SmoothOn { get => Game.Stabilise ?? _s.Settings.StabiliseByDefault; set { Game.Stabilise = value; _s.SaveLibrary(); } }

    // ------------------------------------------------------------------ in-game overlay

    /// <summary>"Default" (global layout), "Custom" (this game's own) or "Off".</summary>
    public string OverlayMode
    {
        get => Game.Overlay is null ? "Default" : Game.Overlay.Enabled ? "Custom" : "Off";
        set
        {
            switch (value)
            {
                case "Default": Game.Overlay = null; break;
                case "Custom":
                    if (Game.Overlay is null || !Game.Overlay.Enabled) { Game.Overlay = _s.Settings.EffectiveOverlay.Clone(); Game.Overlay.Enabled = true; }
                    break;
                case "Off":
                    Game.Overlay ??= _s.Settings.EffectiveOverlay.Clone();
                    Game.Overlay.Enabled = false;
                    break;
            }
            _gameOverlayEditor = null;
            _s.SaveLibrary();
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameOverlayEditor));
            OnPropertyChanged(nameof(OverlayText));
        }
    }

    private OverlayEditorViewModel? _gameOverlayEditor;
    public OverlayEditorViewModel? GameOverlayEditor => Game.Overlay is { } o ? _gameOverlayEditor ??= new OverlayEditorViewModel(o, _s.SaveLibrary) : null;

    public string OverlayText => OverlayMode switch
    {
        "Off" => "No launcher overlay in this game (Afterburner's own, if any, still shows).",
        "Custom" => "This game's own layout.",
        _ => _s.Settings.EffectiveOverlay.Enabled ? "Using your default layout from Settings → RTSS & MSI Afterburner." : "Your default is off (Settings → RTSS & MSI Afterburner).",
    };

    public bool AutoTuneOn
    {
        get => Game.AutoTune.Enabled;
        set { Game.AutoTune.Enabled = value; _s.SaveLibrary(); LoadExtras(); }
    }

    public string BenchmarkHint => $"Press Ctrl+Alt+B while playing to record a {_s.Settings.BenchmarkSeconds}-second benchmark. Do the same run before and after a change to compare.";
    public bool HasBenchmarks => Benchmarks.Count > 0;
    public bool HasAutoTuneSteps => AutoTuneSteps.Count > 0;

    [RelayCommand]
    private void SetPinning(ChoiceOption<CorePinning> option)
    {
        Game.CorePinning = option.Value;
        _s.SaveLibrary();
        LoadExtras();
    }

    [RelayCommand]
    private void ResetAutoTune()
    {
        Game.AutoTune.Steps.Clear();
        Game.AutoTune.TargetReached = false;
        _s.SaveLibrary();
        LoadExtras();
    }

    // ------------------------------------------------------------------ achievements

    public string AchievementsText => Game.Achievements is { } a
        ? (a.Total is > 0 ? $"{a.Earned} of {a.Total} achievements ({a.Earned * 100.0 / a.Total.Value:0}%)" : $"{a.Earned} achievements unlocked") + $" · read {a.Read:d MMM}"
        : "No achievement file found yet. Repacks with a Steam emulator (Goldberg, CODEX, RUNE…) keep one once you unlock something";

    [RelayCommand]
    private async Task RefreshAchievementsAsync()
    {
        var p = await _s.RefreshAchievementsAsync(Game);
        OnPropertyChanged(nameof(AchievementsText));
        SaveAndRefresh();
        if (p is null) Status = "No achievement progress found for this game yet.";
    }

    // ------------------------------------------------------------------ power & pacing

    public bool ShowPower => _s.Settings.PowerControl && _s.Hardware?.IsApu == true;
    public bool PowerOn { get => Game.PowerAuto; set { Game.PowerAuto = value; _s.SaveLibrary(); OnPropertyChanged(nameof(PowerText)); } }
    public string PowerText => !Game.PowerAuto ? "Off for this game: your device's own power setting is used."
        : RyzenAdjClient.Find(_s.Settings.RyzenAdjPath) is null ? "RyzenAdj wasn't found. Set it up in Settings → Power."
        : Game.PowerWatts is { } w ? $"Next session: {w} W (learned from your sessions; range {_s.Settings.PowerMinWatts}-{_s.Settings.PowerMaxWatts} W)"
        : $"Next session: {_s.Settings.PowerDefaultWatts} W to start, then adjusted after each session";

    [RelayCommand]
    private void ResetPower()
    {
        Game.PowerWatts = null;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(PowerText));
    }

    public string LimiterText => Game.LimiterChosen is { } c
        ? $"RTSS limiter: {PacingTuner.Name(c)} (smoother for this game)"
        : Game.LimiterResults.Count > 0 ? $"RTSS limiter: comparing modes, {Game.LimiterResults.Count} of 2 tested"
        : _s.Settings.AutoLimiterMode ? "RTSS limiter: async and front-edge sync get compared over your next two sessions (5 min+)" : "";

    [RelayCommand]
    private void ResetLimiter()
    {
        Game.LimiterResults.Clear();
        Game.LimiterChosen = null;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(LimiterText));
    }

    // ------------------------------------------------------------------ ReShade / ENB

    private List<ModDll> _mods = [];
    public bool HasMods => _mods.Count > 0;
    public string ModsText => _mods.Count == 0 ? "" : $"{string.Join(" + ", _mods.Select(m => m.Kind).Distinct())} found ({string.Join(", ", _mods.Select(m => Path.GetFileName(m.Path)))})";

    public bool ModsOn
    {
        get => ModToggle.AnyEnabled(_mods);
        set
        {
            try
            {
                ModToggle.Set(_mods, value);
                Status = value ? "Visual mods on: next launch uses ReShade/ENB." : "Visual mods off: next launch runs without ReShade/ENB (usually a few FPS more).";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Couldn't switch mods: " + e.Message; }
            DetectMods();
        }
    }

    private void DetectMods()
    {
        var dir = Path.GetDirectoryName(Game.LastScan?.RenderExePath ?? Game.ExePath);
        _mods = dir is null ? [] : ModToggle.Detect(dir, Describe);
        if (Game.LastScan?.RenderExePath is { } render && Path.GetDirectoryName(render) != Path.GetDirectoryName(Game.ExePath))
            _mods.AddRange(ModToggle.Detect(Path.GetDirectoryName(Game.ExePath)!, Describe));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(ModsText));
        OnPropertyChanged(nameof(ModsOn));
    }

    private static string? Describe(string path)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var text = $"{v.FileDescription} {v.ProductName}".Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ display

    public bool AutoDisplayOn { get => Game.AutoDisplay; set { Game.AutoDisplay = value; _s.SaveLibrary(); LoadDisplay(); } }
    public bool HasDisplayProfiles => DisplayProfiles.Count > 0;

    private void LoadDisplay()
    {
        DisplayProfiles.Clear();
        foreach (var (key, p) in Game.DisplayProfiles.OrderByDescending(kv => kv.Value.LastPlayed))
            DisplayProfiles.Add(new DisplayProfileRow(key, p.Label.Length > 0 ? p.Label : key,
                $"render {p.RenderScale}%" + (p.LastAvgFps is { } a ? $" · last {a:0} avg / {p.LastLowFps:0} 1% low" : "") + $" · {p.Sessions} session(s)"));
        OnPropertyChanged(nameof(HasDisplayProfiles));

        if (!_s.Settings.AutoDisplay) { DisplayText = "Off in Settings."; return; }
        if (!Game.AutoDisplay) { DisplayText = "Off for this game: the game's own resolution is used."; return; }
        var displays = HardwareScanner.CurrentDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        if (primary is null) { DisplayText = "No display detected."; return; }
        var plan = DisplayPlanner.Plan(Game, Game.LastScan, primary, _s.Store.LoadSessions(Game.Id), _s.Settings.AllowLowerOutputResolution);
        DisplayText = "At launch: " + plan.Summary + (plan.CanSetResolution ? "" : ". The launcher still tracks FPS per screen.");
    }

    [RelayCommand]
    private void ResetDisplayProfile(DisplayProfileRow row)
    {
        Game.DisplayProfiles.Remove(row.Key);
        _s.SaveLibrary();
        LoadDisplay();
    }

    // ------------------------------------------------------------------ saves

    public bool BackupSavesOn { get => Game.BackupSaves; set { Game.BackupSaves = value; _s.SaveLibrary(); } }
    public bool HasSaveDirs => SaveDirs.Count > 0;
    public bool HasBackups => Backups.Count > 0;

    private void DetectSaves(bool quiet)
    {
        var found = SaveLocator.FindCandidates(Game, Game.LastScan, _s.SaveContext);
        var added = found.Where(f => !Game.SaveDirs.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        Game.SaveDirs.AddRange(added);
        if (added.Count > 0) _s.SaveLibrary();
        SaveStatus = added.Count > 0 ? $"Found {added.Count} save folder(s). Check they're right, and remove any that aren't."
            : quiet ? "" : "No save folder found automatically. Add it with \"Add folder\" (check the repack's NFO or PCGamingWiki).";
        LoadExtras();
    }

    [RelayCommand] private void FindSaves() => DetectSaves(quiet: false);

    [RelayCommand]
    private void AddSaveDir()
    {
        var dlg = new OpenFolderDialog { Title = $"Pick the folder where {Game.Name} keeps its saves" };
        if (dlg.ShowDialog() != true) return;
        if (!Game.SaveDirs.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) Game.SaveDirs.Add(dlg.FolderName);
        _s.SaveLibrary();
        LoadExtras();
    }

    [RelayCommand]
    private void RemoveSaveDir(string dir)
    {
        Game.SaveDirs.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        _s.SaveLibrary();
        LoadExtras();
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            var r = await Task.Run(() => _s.SaveBackups.Backup(Game, _s.BackupTargets(), _s.Settings.Backups.KeepPerGame, force: true));
            SaveStatus = r.Message;
            _s.SaveLibrary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveStatus = "Backup failed: " + e.Message; }
        LoadBackups();
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupRow row)
    {
        if (MessageBox.Show($"Restore {Game.Name}'s saves from {row.When}?\n\nYour current saves are backed up first, so you can undo this.",
                "Restore saves", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var r = await Task.Run(() => _s.SaveBackups.Restore(Game, row.Path, _s.BackupTargets(), _s.Settings.Backups.KeepPerGame));
            SaveStatus = r.Message;
            _s.SaveLibrary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { SaveStatus = "Restore failed: " + e.Message; }
        LoadBackups();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var dir = _s.SaveBackups.GameFolder(Game);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var b in _s.SaveBackups.List(Game).Take(10))
            Backups.Add(new BackupRow(b.Taken.ToString("ddd d MMM, HH:mm"), b.Bytes < 1024 * 1024 ? $"{b.Bytes / 1024.0:0} KB" : $"{b.Bytes / 1024.0 / 1024:0.0} MB", b.Path));
        OnPropertyChanged(nameof(HasBackups));
        if (SaveStatus.Length == 0 && Game.LastSaveBackup is { } last) SaveStatus = $"Last backup {last:ddd d MMM, HH:mm}.";
    }

    // ------------------------------------------------------------------ runtimes

    [RelayCommand]
    private async Task CheckRuntimesAsync()
    {
        RuntimeStatus = "Checking bundled runtimes…";
        var installers = Game.LastScan?.RedistInstallers ?? [];
        var rows = await Task.Run(() =>
        {
            var infos = RedistCatalog.Dedupe(installers.Select(RedistService.Classify).Where(r => r is not null)!.Cast<RedistInfo>());
            return infos.Select(i => (info: i, installed: _s.Redists.IsInstalled(i))).ToList();
        });
        Runtimes.Clear();
        foreach (var (info, installed) in rows) Runtimes.Add(new RuntimeRow(info) { Installed = installed, State = installed ? "Installed" : "Missing" });
        RuntimesChecked = true;
        var missing = Runtimes.Count(r => !r.Installed);
        RuntimeStatus = Runtimes.Count == 0 ? "This repack doesn't bundle any runtimes the launcher recognises."
            : missing == 0 ? "All bundled runtimes are already installed."
            : $"{missing} missing. A missing runtime is the most common reason a repack won't start.";
        OnPropertyChanged(nameof(HasMissingRuntimes));
    }

    [RelayCommand]
    private async Task InstallMissingRuntimesAsync()
    {
        foreach (var row in Runtimes.Where(r => !r.Installed).ToList())
        {
            row.State = "Installing…";
            RuntimeStatus = $"Installing {row.Name}…";
            var (ok, msg) = await RedistService.InstallAsync(row.Info);
            row.Installed = ok;
            row.State = ok ? msg : "Failed: " + msg;
            _s.Log.Write($"[{Game.Name}] runtime {row.Name}: {msg}");
        }
        RuntimeStatus = Runtimes.All(r => r.Installed) ? "Done. Try launching the game." : "Some runtimes failed. Run their installers manually from the game folder.";
        OnPropertyChanged(nameof(HasMissingRuntimes));
    }

    public bool HasMissingRuntimes => Runtimes.Any(r => !r.Installed);
}
