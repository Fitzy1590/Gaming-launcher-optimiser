using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

/// <summary>Edits an overlay layout (global or one game's), with a preview of the line it produces.</summary>
public sealed partial class OverlayEditorViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public OverlaySettings O { get; }

    public OverlayEditorViewModel(OverlaySettings o, Action save)
    {
        O = o;
        _save = save;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _save(); };
    }

    private void Set(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change();
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Preview));
    }

    public bool Enabled { get => O.Enabled; set => Set(() => O.Enabled = value); }
    public OverlayArrangement Arrangement { get => O.Arrangement; set => Set(() => O.Arrangement = value); }
    public bool Fps { get => O.Fps; set => Set(() => O.Fps = value); }
    public bool OnePercentLow { get => O.OnePercentLow; set => Set(() => O.OnePercentLow = value); }
    public bool Watts { get => O.Watts; set => Set(() => O.Watts = value); }
    public bool CpuTemp { get => O.CpuTemp; set => Set(() => O.CpuTemp = value); }
    public bool GpuTemp { get => O.GpuTemp; set => Set(() => O.GpuTemp = value); }
    public bool GpuLoad { get => O.GpuLoad; set => Set(() => O.GpuLoad = value); }
    public bool Cap { get => O.Cap; set => Set(() => O.Cap = value); }
    public bool Clock { get => O.Clock; set => Set(() => O.Clock = value); }
    public bool Limit { get => O.Limit; set => Set(() => O.Limit = value); }
    public bool AvoidDuplicates { get => O.AvoidDuplicates; set => Set(() => O.AvoidDuplicates = value); }
    public int LeftMargin { get => O.LeftMargin; set => Set(() => O.LeftMargin = Math.Clamp(value, 0, 12)); }
    /// <summary>Another overlay (OneXConsole's, which Fitz can't see) already shows FPS, power and GPU temperature: keep only what it doesn't.</summary>
    [RelayCommand]
    private void ExtrasOnly()
    {
        O.Fps = false; O.Watts = false; O.GpuTemp = false;
        O.CpuTemp = true; O.Limit = true; O.OnePercentLow = true;
        _saveTimer.Stop(); _saveTimer.Start();
        OnPropertyChanged(string.Empty);
    }

    public bool Coloured { get => O.Coloured; set => Set(() => O.Coloured = value); }
    public int SizePercent { get => O.SizePercent; set => Set(() => O.SizePercent = value); }

    /// <summary>What the overlay will look like (plain text).</summary>
    public string Preview
    {
        get
        {
            var plain = O.Clone();
            plain.Coloured = false;
            var text = OverlayFormatter.Format(plain, new OverlayValues(117, 98, 45, 77, 75, 96, 117, Now: DateTime.Now));
            return text.Length == 0 ? "(nothing selected)" : text;
        }
    }
}
