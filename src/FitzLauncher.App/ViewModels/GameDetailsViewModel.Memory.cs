using CommunityToolkit.Mvvm.ComponentModel;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

/// <summary>Pre-launch badge: warns when the memory reserved for graphics leaves too little for games (APUs and handhelds).</summary>
public sealed partial class GameDetailsViewModel
{
    [ObservableProperty] private string _memoryBadgeTitle = "";
    [ObservableProperty] private string _memoryBadgeText = "";
    [ObservableProperty] private bool _hasMemoryBadge;

    private async Task LoadMemoryBadgeAsync()
    {
        try
        {
            var report = await Task.Run(_s.CheckApuMemory);
            if (_alive.IsCancellationRequested) return;
            HasMemoryBadge = report.ShowBadge;
            MemoryBadgeTitle = report.Headline;
            MemoryBadgeText = report.ShowBadge ? $"{report.Detail} {report.Advice}" : "";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            HasMemoryBadge = false; // a diagnostic must never break the page
        }
    }
}
