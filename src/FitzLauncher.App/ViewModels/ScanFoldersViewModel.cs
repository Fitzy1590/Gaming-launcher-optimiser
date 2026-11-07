using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

/// <summary>One game found by a folder scan: tick it, rename it, or pick a different exe before adding.</summary>
public sealed partial class FoundGameRow : ObservableObject
{
    private readonly Action _changed;
    public FoundGame Found { get; }

    public FoundGameRow(FoundGame found, string? storageNote, Action changed)
    {
        Found = found;
        _changed = changed;
        _name = found.Name;
        _exe = found.Exe;
        _isSelected = found.SuggestAdd;
        StorageNote = storageNote;
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _exe;

    public string? StorageNote { get; }
    public string Folder => Found.Folder;
    public bool CanAdd => Exe is not null && Found.Status != FoundGameStatus.AlreadyInLibrary;
    public bool HasChoices => Found.Candidates.Count > 1;
    public string ExeDisplay => Exe is null ? "no exe" : Path.GetRelativePath(Found.Folder, Exe);

    public string StatusText => Found.Status switch
    {
        FoundGameStatus.New => StorageNote is null ? "New" : "New · " + StorageNote,
        FoundGameStatus.AlreadyInLibrary => "Already in library",
        FoundGameStatus.NotInstalled => "Not installed (repack installer)",
        _ => "No exe found",
    };

    public string StatusLevel => Found.Status switch
    {
        FoundGameStatus.New => StorageNote is null ? "None" : "Medium",
        FoundGameStatus.AlreadyInLibrary => "Low",
        _ => "High",
    };

    public string? Hint => Found.Note ?? (HasChoices ? "Click the exe name to try the next candidate, or Browse to pick one." : null);

    partial void OnIsSelectedChanged(bool value) => _changed();
    partial void OnExeChanged(string? value)
    {
        OnPropertyChanged(nameof(ExeDisplay));
        OnPropertyChanged(nameof(CanAdd));
        if (value is not null && Found.Status != FoundGameStatus.AlreadyInLibrary) IsSelected = true;
        _changed();
    }

    /// <summary>Cycles through the exe candidates (best guess first).</summary>
    [RelayCommand]
    private void NextExe()
    {
        if (Found.Candidates.Count == 0) return;
        var i = Exe is null ? -1 : Found.Candidates.IndexOf(Exe);
        Exe = Found.Candidates[(i + 1) % Found.Candidates.Count];
    }

    [RelayCommand]
    private void BrowseExe()
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Pick the exe that starts {Name}",
            Filter = "Executables (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(Found.Folder) ? Found.Folder : null,
        };
        if (dlg.ShowDialog() == true) Exe = dlg.FileName;
    }
}

/// <summary>
/// "Scan game folders": pick one or more folders, review everything found, and add only what you tick.
/// The folders are remembered, so a later scan (or start-up check) shows new games only.
/// </summary>
public sealed partial class ScanFoldersViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<FoundGameRow> Results { get; } = [];
    public ICollectionView ResultsView { get; }
    public event Action? RequestClose;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private bool _showAlreadyAdded;
    [ObservableProperty] private string _summary = "Add the folder(s) where your games are installed, then press Scan.";

    public ScanFoldersViewModel(AppServices s, MainViewModel main)
    {
        _s = s;
        _main = main;
        foreach (var f in s.Settings.LibraryFolders) Folders.Add(f);
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.Filter = o => o is FoundGameRow r && (ShowAlreadyAdded || r.Found.Status != FoundGameStatus.AlreadyInLibrary);
    }

    public int SelectedCount => Results.Count(r => r.IsSelected && r.CanAdd);
    public string AddButtonText => SelectedCount == 1 ? "Add 1 game" : $"Add {SelectedCount} games";
    public bool CanAddAny => SelectedCount > 0 && !IsScanning;
    public bool HasFolders => Folders.Count > 0;

    partial void OnShowAlreadyAddedChanged(bool value) => ResultsView.Refresh();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanAddAny));

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(CanAddAny));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Pick a folder with your games (or a single game's folder)", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FolderNames.Where(f => !Folders.Contains(f, StringComparer.OrdinalIgnoreCase))) Folders.Add(f);
        SaveFolders();
        await ScanAsync();
    }

    [RelayCommand]
    private void RemoveFolder(string folder)
    {
        Folders.Remove(folder);
        SaveFolders();
        foreach (var r in Results.Where(r => r.Folder.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList()) Results.Remove(r);
        SelectionChanged();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (Folders.Count == 0) { Summary = "Add a folder first."; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true;
        Results.Clear();
        var exes = _s.Library.Games.Select(g => g.ExePath).ToList();
        var dirs = _s.Library.Games.Select(g => g.InstallDir).ToList();
        var progress = new Progress<string>(name => Progress = $"Scanning {name}…");
        try
        {
            foreach (var folder in Folders.ToList())
            {
                var found = await Task.Run(() => LibraryFolderScanner.Scan(folder, exes, dirs, GameImporter.ProductName, progress, ct), ct);
                foreach (var f in found) Results.Add(new FoundGameRow(f, StorageNote(f.Folder), SelectionChanged));
            }
            var n = Results.Count(r => r.Found.Status == FoundGameStatus.New);
            var installers = Results.Count(r => r.Found.Status == FoundGameStatus.NotInstalled);
            var already = Results.Count(r => r.Found.Status == FoundGameStatus.AlreadyInLibrary);
            Summary = $"{n} new game(s) found" + (installers > 0 ? $", {installers} repack installer(s) not installed yet" : "") +
                      (already > 0 ? $", {already} already in your library" : "") + ". Untick anything you don't want, then Add.";
        }
        catch (OperationCanceledException) { Summary = "Scan cancelled."; }
        finally
        {
            IsScanning = false;
            Progress = "";
            SelectionChanged();
        }
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Results.Where(r => r.CanAdd)) r.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in Results) r.IsSelected = false; }

    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        var games = Results.Where(r => r.IsSelected && r.CanAdd)
            .Select(r => GameImporter.FromExe(r.Exe!, string.IsNullOrWhiteSpace(r.Name) ? r.Found.Name : r.Name.Trim()))
            .ToList();
        RequestClose?.Invoke();
        await _main.AddGamesAsync(games);
    }

    public void Cancel() => _cts?.Cancel();

    private void SaveFolders()
    {
        _s.Settings.LibraryFolders = [.. Folders];
        _s.SaveSettings();
        OnPropertyChanged(nameof(HasFolders));
    }

    private string? StorageNote(string folder)
    {
        if (folder.Length < 2 || _s.Hardware is null) return null;
        var drive = _s.Hardware.Drives.FirstOrDefault(d => d.Letter.Equals(folder[..2], StringComparison.OrdinalIgnoreCase));
        return drive?.Kind switch
        {
            DriveKind.SdCard => "on SD card (slow: may stutter)",
            DriveKind.Hdd => "on hard drive (slow: may stutter)",
            _ => null,
        };
    }
}
