using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

public sealed class UpdateHistoryRow(GameUpdateRecord r, GameDetailsViewModel owner)
{
    public string Title => $"{r.When.ToLocalTime():d MMM yyyy HH:mm} · {(r.Success ? "Updated" : "Failed")}{(r.FromVersion is { } f ? $" from {f}" : "")}{(r.ToVersion is { } t ? $" to {t}" : "")}{(r.RolledBack ? " · restored" : "")}";
    public string Detail => $"{Path.GetFileName(r.SourceFile)} · {r.Notes}";
    public bool CanRestore => r.Success && !r.RolledBack && Directory.Exists(r.BackupDir);
    public IAsyncRelayCommand RestoreCommand { get; } = new AsyncRelayCommand(() => owner.RestoreUpdateAsync(r));
}

public sealed class GuideLinkRow(GuideLink l)
{
    public string Label => l.Label;
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => ModsViewModel.Open(l.Url));
}

/// <summary>Game page: official update check, install an update file safely, history/restore, manual guide.</summary>
public sealed partial class GameDetailsViewModel
{
    /// <summary>Set by the main window so updates never run while the game is open.</summary>
    public Func<bool>? IsPlayingCheck { get; init; }

    public ObservableCollection<UpdateHistoryRow> UpdateHistory { get; } = [];
    public ObservableCollection<string> GuideSteps { get; } = [];
    public ObservableCollection<GuideLinkRow> GuideLinks { get; } = [];
    [ObservableProperty] private string _installedVersionInput = "";
    [ObservableProperty] private bool _hasGuide;
    [ObservableProperty] private string _guideTitle = "";
    [ObservableProperty] private string _guideReason = "";

    public string UpdateStatus => Game.Updates.State switch
    {
        UpdateState.Available => "Update available",
        UpdateState.ProbablyAvailable => "Probably an update available",
        UpdateState.UpToDate => "Up to date",
        UpdateState.CantCheck => "Can't check",
        _ => Game.Updates.CheckedAt is null ? "Not checked yet" : "Unknown",
    };
    public string UpdateDetail => Game.Updates.Detail ?? "Checks the official Steam or GOG version once a day.";
    public string LatestText => Game.Updates.LatestVersion is null && Game.Updates.LatestReleased is null ? ""
        : $"Latest official: {Game.Updates.LatestVersion ?? "build"}{(Game.Updates.LatestReleased is { } d ? $" ({d:d MMM yyyy})" : "")}{(Game.Updates.Source is { } src ? $" · {src}" : "")}" +
          (Game.Updates.CheckedAt is { } c ? $" · checked {c.ToLocalTime():d MMM HH:mm}" : "");
    public bool HasPatchNotes => Game.Updates.PatchNotesUrl is not null;
    public bool HasStorePage => Game.Updates.StoreUrl is not null;
    public bool UpdateIsAvailable => Game.Updates.State is UpdateState.Available or UpdateState.ProbablyAvailable;

    private void LoadUpdates()
    {
        InstalledVersionInput = Game.Updates.InstalledVersionOverride ?? _s.GameUpdates.InstalledVersion(Game) ?? "";
        UpdateHistory.Clear();
        foreach (var r in Game.Updates.History.Take(10)) UpdateHistory.Add(new UpdateHistoryRow(r, this));
        foreach (var p in new[] { nameof(UpdateStatus), nameof(UpdateDetail), nameof(LatestText), nameof(HasPatchNotes), nameof(HasStorePage), nameof(UpdateIsAvailable) }) OnPropertyChanged(p);
        Card.Refresh();
    }

    [RelayCommand]
    private Task CheckGameUpdateAsync() => Busy("Checking the official version…", async () =>
    {
        await _s.GameUpdates.CheckAsync(Game);
        _s.SaveLibrary();
        LoadUpdates();
        Status = $"{UpdateStatus}. {UpdateDetail}";
    });

    [RelayCommand]
    private Task SaveInstalledVersionAsync()
    {
        Game.Updates.InstalledVersionOverride = string.IsNullOrWhiteSpace(InstalledVersionInput) ? null : InstalledVersionInput.Trim().TrimStart('v', 'V');
        _s.SaveLibrary();
        return CheckGameUpdateAsync();
    }

    [RelayCommand]
    private async Task InstallGameUpdateAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Pick the update for {Game.Name} (installer .exe, or a .zip/.7z/.rar of game files)",
            Filter = "Game updates|*.exe;*.zip;*.7z;*.rar|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var root = Core.Scanning.GameScanner.ResolveInstallRoot(Game);
        if (dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Clipboard.SetText(root);
            if (MessageBox.Show($"The launcher will copy the whole game as a backup, then run the installer.\n\nIf it asks where the game is, paste this (it's on the clipboard):\n{root}\n\nContinue?",
                    "Fitz Launcher · update", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        }
        UpdateResult result = new(false, "");
        await Busy("Updating…", async () =>
        {
            result = await _s.GameUpdates.InstallAsync(Game, dlg.FileName, new Progress<string>(m => BusyText = m), IsPlayingCheck?.Invoke() == true);
        });
        LoadUpdates();
        Status = result.Message;
        if (result.Guide is { } guide)
        {
            ShowGuide(guide);
            MessageBox.Show($"{result.Message}\n\nThe game page now shows how to update it by hand, with official links.", "Fitz Launcher · update didn't finish", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task RestoreUpdateAsync(GameUpdateRecord r)
    {
        if (MessageBox.Show($"Put {Game.Name} back to {r.FromVersion ?? "the version before this update"}? Files changed by the update are replaced with the backup.",
                "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UpdateResult result = new(false, "");
        await Busy("Restoring the previous version…", async () => result = await _s.GameUpdates.RestoreAsync(Game, r));
        LoadUpdates();
        Status = result.Message;
    }

    private void ShowGuide(UpdateGuide g)
    {
        GuideTitle = g.Title;
        GuideReason = g.Reason;
        GuideSteps.Clear();
        for (var i = 0; i < g.Steps.Count; i++) GuideSteps.Add($"{i + 1}. {g.Steps[i]}");
        GuideLinks.Clear();
        foreach (var l in g.Links) GuideLinks.Add(new GuideLinkRow(l));
        HasGuide = true;
    }

    [RelayCommand] private void ShowUpdateGuide() => ShowGuide(_s.GameUpdates.Guide(Game, UpdateDetail));
    [RelayCommand] private void CloseGuide() => HasGuide = false;
    [RelayCommand] private void OpenPatchNotes() { if (Game.Updates.PatchNotesUrl is { } u) ModsViewModel.Open(u); }
    [RelayCommand] private void OpenStorePage() { if (Game.Updates.StoreUrl is { } u) ModsViewModel.Open(u); }
}
