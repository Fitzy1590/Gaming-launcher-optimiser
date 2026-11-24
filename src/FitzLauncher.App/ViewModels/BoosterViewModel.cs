using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Windows.Optimization;

namespace FitzLauncher.App.ViewModels;

/// <summary>
/// The Booster page (SPEC §17 G4): read-only view of what Game Mode has set, plus the Export Diagnostic Package button.
/// It changes nothing itself; the settings stay where they were (Settings → Game Mode).
/// </summary>
public sealed partial class BoosterViewModel(AppServices s, Func<IReadOnlyCollection<int>> gamePids, Action<string> toast) : ObservableObject
{
    [ObservableProperty] private string _timerText = "Checking…";
    [ObservableProperty] private string _powerPlanText = "Checking…";
    [ObservableProperty] private string _affinityText = "Checking…";
    [ObservableProperty] private string _exportNote = "";

    public string TraceText => s.Diag.Enabled ? "On" : "Off";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var pids = gamePids();
        var (timer, plan, masks) = await Task.Run(() => (DiagnosticFacts.TimerText(), DiagnosticFacts.PowerPlanText(), DiagnosticFacts.AffinityMasks(pids)));
        TimerText = timer;
        PowerPlanText = plan;
        AffinityText = masks.Count == 0 ? "No game running" : string.Join(Environment.NewLine, masks.Select(m => $"{m.Name}: {m.Value}"));
        OnPropertyChanged(nameof(TraceText));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var pids = gamePids();
        try
        {
            var path = await Task.Run(() => s.ExportDiagnostics(pids));
            ExportNote = $"Saved to {path}. Secrets are hidden, but have a look before you share it.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ExportNote = "Couldn't save the report. Check that the launcher's data folder can be written to.";
            toast(ExportNote);
        }
    }
}
