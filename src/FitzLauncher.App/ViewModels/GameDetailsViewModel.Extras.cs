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

    private void LoadExtras()
    {
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
                AutoTuneOutcome.Kept => "✓ kept", AutoTuneOutcome.Reverted => "↶ reverted", AutoTuneOutcome.Skipped => "skipped", _ => "testing",
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
        foreach (var (info, installed) in rows) Runtimes.Add(new RuntimeRow(info) { Installed = installed, State = installed ? "✓ installed" : "missing" });
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
            row.State = "installing…";
            RuntimeStatus = $"Installing {row.Name}…";
            var (ok, msg) = await RedistService.InstallAsync(row.Info);
            row.Installed = ok;
            row.State = ok ? "✓ " + msg.ToLowerInvariant() : "✗ " + msg;
            _s.Log.Write($"[{Game.Name}] runtime {row.Name}: {msg}");
        }
        RuntimeStatus = Runtimes.All(r => r.Installed) ? "Done. Try launching the game." : "Some runtimes failed. Run their installers manually from the game folder.";
        OnPropertyChanged(nameof(HasMissingRuntimes));
    }

    public bool HasMissingRuntimes => Runtimes.Any(r => !r.Installed);
}
