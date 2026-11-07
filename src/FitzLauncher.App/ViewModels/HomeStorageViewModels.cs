using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Stats;
using FitzLauncher.Core.Storage;
using FitzLauncher.Windows;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

public sealed record AttentionItem(string Text, Page Go, string Action);

/// <summary>Start page: pick up where you left off, see the week at a glance, fix what needs fixing.</summary>
public sealed partial class HomeViewModel(AppServices s, MainViewModel main) : ObservableObject
{
    public ObservableCollection<GameViewModel> ContinuePlaying { get; } = [];
    public ObservableCollection<GameViewModel> RecentlyAdded { get; } = [];
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    [ObservableProperty] private string _weekHours = "0";
    [ObservableProperty] private string _weekSessions = "0";
    [ObservableProperty] private string _weekOnTarget = "–";
    [ObservableProperty] private string _totalHours = "0";

    public bool HasContinue => ContinuePlaying.Count > 0;
    public bool HasRecent => RecentlyAdded.Count > 0;
    public bool HasAttention => Attention.Count > 0;
    public string Greeting => DateTime.Now.ToString("dddd d MMMM");

    public void Reload()
    {
        var visible = main.Games.Where(g => !g.Game.Hidden).ToList();
        ContinuePlaying.Clear();
        foreach (var g in visible.Where(g => g.Game.LastPlayed is not null).OrderByDescending(g => g.Game.LastPlayed).Take(4)) ContinuePlaying.Add(g);
        RecentlyAdded.Clear();
        foreach (var g in visible.Where(g => !ContinuePlaying.Contains(g)).OrderByDescending(g => g.Game.AddedAt).Take(6)) RecentlyAdded.Add(g);

        var sessions = s.Store.LoadSessions();
        var week = sessions.Where(x => x.End >= DateTimeOffset.Now.AddDays(-7)).ToList();
        WeekHours = LibraryStats.FormatHours(week.Sum(x => x.Duration.TotalHours));
        WeekSessions = week.Count.ToString();
        var withTarget = week.Where(x => x.TimeAtTarget is not null).ToList();
        WeekOnTarget = withTarget.Count == 0 ? "–" : $"{withTarget.Average(x => x.TimeAtTarget!.Value):P0}";
        TotalHours = LibraryStats.FormatHours(s.Library.Games.Sum(g => g.TotalPlayTime.TotalHours));

        Attention.Clear();
        if (main.HasUpdate) Attention.Add(new AttentionItem(main.UpdateText, Page.Settings, "Update"));
        var slowDrives = s.Hardware?.Drives.Where(d => d.Kind is DriveKind.SdCard or DriveKind.Hdd).Select(d => d.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var onSlow = s.Library.Games.Count(g => g.InstallDir.Length >= 2 && slowDrives.Contains(g.InstallDir[..2]));
        if (onSlow > 0) Attention.Add(new AttentionItem($"{onSlow} game(s) on slow storage (SD card / hard drive). Moving them to the NVMe drive cuts stutter", Page.Storage, "Storage"));
        var noArt = s.Library.Games.Count(g => g.ArtworkPath is null);
        if (noArt > 0) Attention.Add(new AttentionItem($"{noArt} game(s) without cover art. Open a game, then Edit → Choose image", Page.Library, "Library"));
        var played = s.Library.Games.Where(g => g.LastPlayed is not null && g.SaveDirs.Count == 0 && g.BackupSaves).ToList();
        if (played.Count > 0) Attention.Add(new AttentionItem($"{played.Count} played game(s) have no save folder set, so they aren't being backed up", Page.Library, "Library"));
        foreach (var d in s.Hardware?.Drives.Where(d => d.TotalBytes > 0 && d.FreeBytes < d.TotalBytes * 0.08) ?? [])
            Attention.Add(new AttentionItem($"Drive {d.Letter} is almost full", Page.Storage, "Storage"));
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand] private void Play(GameViewModel g) => _ = main.PlayAsync(g);

    [RelayCommand]
    private void Open(GameViewModel g)
    {
        main.Page = Page.Library;
        main.Selected = g;
    }

    [RelayCommand] private void Go(AttentionItem item) => main.Page = item.Go;
}

public sealed partial class DriveRow : ObservableObject
{
    public required string Letter { get; init; }
    public required string Kind { get; init; }
    public required string Space { get; init; }
    public double UsedPercent { get; init; }
    public bool IsSlow { get; init; }
}

public sealed partial class StorageGameRow(GameViewModel card) : ObservableObject
{
    public GameViewModel Card { get; } = card;
    public Game Game => Card.Game;
    public string Name => Game.Name;
    [ObservableProperty] private long _bytes = -1;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _driveLabel = "";
    [ObservableProperty] private bool _isSlow;
    public string Size => Bytes < 0 ? "…" : Bytes >= 1L << 30 ? $"{Bytes / (double)(1L << 30):0.0} GB" : $"{Bytes / (double)(1L << 20):0} MB";
    partial void OnBytesChanged(long value) => OnPropertyChanged(nameof(Size));
}

/// <summary>Storage: what's using the space, and moving, checking or uninstalling games.</summary>
public sealed partial class StorageViewModel(AppServices s, MainViewModel main) : ObservableObject
{
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<StorageGameRow> Games { get; } = [];
    [ObservableProperty] private string _summary = "";

    private string BaselinePath(Game g) => Path.Combine(s.Paths.Root, "integrity", g.Id.ToString("N") + ".json");

    public async Task ReloadAsync()
    {
        Drives.Clear();
        var known = s.Hardware?.Drives ?? [];
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var letter = d.Name[..2];
            var kind = known.FirstOrDefault(k => k.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))?.Kind ?? DriveKind.Unknown;
            Drives.Add(new DriveRow
            {
                Letter = letter,
                Kind = kind switch { DriveKind.NvmeSsd => "NVMe SSD", DriveKind.SataSsd => "SSD", DriveKind.Hdd => "Hard drive", DriveKind.SdCard => "SD card", _ => d.DriveType == DriveType.Removable ? "Removable" : "Drive" },
                Space = $"{(d.TotalSize - d.AvailableFreeSpace) / 1e9:0} of {d.TotalSize / 1e9:0} GB used · {d.AvailableFreeSpace / 1e9:0} GB free",
                UsedPercent = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize,
                IsSlow = kind is DriveKind.SdCard or DriveKind.Hdd,
            });
        }

        Games.Clear();
        foreach (var card in main.Games) Games.Add(Row(card));
        await Task.WhenAll(Games.Where(r => r.Bytes < 0).Select(async r => r.Bytes = await Task.Run(() => GameMover.FolderSize(r.Game.InstallDir))));
        var sorted = Games.OrderByDescending(r => r.Bytes).ToList();
        Games.Clear();
        foreach (var r in sorted) Games.Add(r);
        Summary = $"{Games.Count} games · {Games.Sum(r => Math.Max(r.Bytes, 0)) / 1e9:0} GB in total";
    }

    private StorageGameRow Row(GameViewModel card)
    {
        var g = card.Game;
        var letter = g.InstallDir.Length >= 2 ? g.InstallDir[..2] : "";
        var drive = Drives.FirstOrDefault(d => d.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
        return new StorageGameRow(card)
        {
            Bytes = g.LastScan?.TotalBytes is > 0 and var b ? b : -1,
            DriveLabel = drive is null ? letter : $"{letter} {drive.Kind}",
            IsSlow = drive?.IsSlow ?? false,
        };
    }

    [RelayCommand]
    private async Task MoveAsync(StorageGameRow row)
    {
        if (main.IsPlaying) { row.Status = "Finish playing first"; return; }
        var dlg = new OpenFolderDialog { Title = $"Move {row.Name} into which folder? (e.g. C:\\Games on the NVMe drive)" };
        if (dlg.ShowDialog() != true) return;
        var dest = dlg.FolderName;
        var free = new DriveInfo(Path.GetPathRoot(dest)!).AvailableFreeSpace;
        if (MessageBox.Show($"Move {row.Name} ({row.Size}) to {dest}?\n\nFiles are copied and checked first; the old copy is deleted only once everything matches.",
                "Move game", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        row.IsBusy = true;
        var progress = new Progress<(long done, long total, string file)>(p => row.Status = $"Moving {p.done * 100 / Math.Max(p.total, 1)}%");
        var result = await Task.Run(() => GameMover.Move(row.Game, dest, free, deleteSource: true, progress));
        row.IsBusy = false;
        row.Status = result.Message;
        if (!result.Ok) return;
        s.SaveLibrary();
        row.Card.Refresh();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task VerifyAsync(StorageGameRow row)
    {
        row.IsBusy = true;
        try
        {
            var g = row.Game;
            var progress = new Progress<(int done, int total)>(p => row.Status = $"Checking {p.done * 100 / Math.Max(p.total, 1)}%");
            // Checksums the repack shipped come first.
            var lists = await Task.Run(() => IntegrityChecker.FindChecksumFiles(g.InstallDir));
            if (lists.Count > 0)
            {
                var reports = new List<IntegrityReport>();
                foreach (var l in lists) reports.Add(await Task.Run(() => IntegrityChecker.VerifyChecksumFile(l, progress)));
                Show(row, new IntegrityReport(reports.Sum(r => r.Checked), reports.SelectMany(r => r.Issues).ToList(), "repack checksums"));
                return;
            }
            var baseline = IntegrityChecker.LoadBaseline(BaselinePath(g));
            if (baseline is null)
            {
                if (MessageBox.Show($"{row.Name} has no checksum list from its repack.\n\nRecord the current files now as a known-good baseline? Later checks will then spot files that go missing (for example, removed by antivirus) or change size.",
                        "Verify files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { row.Status = ""; return; }
                var b = await Task.Run(() => IntegrityChecker.CreateBaseline(g.InstallDir, hash: false, progress));
                IntegrityChecker.SaveBaseline(BaselinePath(g), b);
                row.Status = $"Baseline saved ({b.Files.Count:N0} files). Verify again any time";
                return;
            }
            Show(row, await Task.Run(() => IntegrityChecker.VerifyBaseline(g.InstallDir, baseline, full: false, progress)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { row.Status = "Check failed: " + e.Message; }
        finally { row.IsBusy = false; }
    }

    private static void Show(StorageGameRow row, IntegrityReport report)
    {
        row.Status = report.Summary;
        if (report.Ok) return;
        var list = string.Join("\n", report.Issues.Take(15).Select(i => $"{i.Kind}: {i.RelativePath}"));
        MessageBox.Show($"{report.Summary}\n\n{list}{(report.Issues.Count > 15 ? "\n…" : "")}\n\nMissing .dll/.exe files are often removed by antivirus. Restore them from the repack, or add an exclusion and reinstall.",
            $"{row.Name}: file check", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    [RelayCommand]
    private async Task UninstallAsync(StorageGameRow row)
    {
        if (main.IsPlaying) { row.Status = "Finish playing first"; return; }
        var g = row.Game;
        if (MessageBox.Show($"Uninstall {row.Name} ({row.Size})?\n\n• Saves are backed up first ({(g.SaveDirs.Count > 0 ? "found" : "no save folder set")})\n• The launcher's changes are reverted\n• The game folder goes to the Recycle Bin (if it fits; Windows asks otherwise)\n• It's removed from your library",
                "Uninstall", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        row.IsBusy = true;
        row.Status = "Backing up saves…";
        if (g.SaveDirs.Count > 0)
            await Task.Run(() => s.SaveBackups.Backup(g, s.BackupTargets(), s.Settings.Backups.KeepPerGame, force: true));
        row.Status = "Reverting changes…";
        await Task.Run(() => s.Tweaks.RevertAll(g.AppliedChanges, g));
        row.Status = "Deleting…";
        var deleted = await Task.Run(() => RecycleBin.Delete(g.InstallDir.TrimEnd('\\')));
        row.IsBusy = false;
        if (!deleted && Directory.Exists(g.InstallDir)) { row.Status = "Delete cancelled or failed; the game is still installed"; return; }
        main.RemoveFromLibrary(row.Card);
        Games.Remove(row);
        Summary = $"{row.Name} uninstalled (saves backed up, folder in the Recycle Bin)";
    }

    [RelayCommand]
    private void OpenFolder(StorageGameRow row)
    {
        if (Directory.Exists(row.Game.InstallDir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{row.Game.InstallDir}\"") { UseShellExecute = true });
    }
}
