using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

/// <summary>One AMD feature with Leave / On / Off.</summary>
public sealed partial class AmdFeatureRow(string name, string hint, Func<bool?> get, Action<bool?> set) : ObservableObject
{
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    public bool IsLeave { get => get() is null; set { if (value) { set(null); Notify(); } } }
    public bool IsOn { get => get() == true; set { if (value) { set(true); Notify(); } } }
    public bool IsOff { get => get() == false; set { if (value) { set(false); Notify(); } } }
    private void Notify() { OnPropertyChanged(nameof(IsLeave)); OnPropertyChanged(nameof(IsOn)); OnPropertyChanged(nameof(IsOff)); }
}

/// <summary>Game page: AMD Software features applied while this game runs.</summary>
public sealed partial class GameDetailsViewModel
{
    public bool IsAmdGpu => _s.Hardware?.PrimaryGpu?.Vendor == GpuVendor.Amd;
    public ObservableCollection<AmdFeatureRow> AmdRows { get; } = [];
    [ObservableProperty] private string _amdSupportText = "";

    public int AmdChillMin { get => Game.Amd.ChillMinFps; set { Game.Amd.ChillMinFps = Math.Clamp(value, 30, 300); _s.SaveLibrary(); } }
    public int AmdChillMax { get => Game.Amd.ChillMaxFps; set { Game.Amd.ChillMaxFps = Math.Clamp(value, 30, 300); _s.SaveLibrary(); } }
    public int AmdSharpening { get => Game.Amd.SharpeningPercent; set { Game.Amd.SharpeningPercent = Math.Clamp(value, 10, 100); _s.SaveLibrary(); OnPropertyChanged(); } }

    private void LoadAmd()
    {
        if (AmdRows.Count > 0 || !IsAmdGpu) return;
        var a = Game.Amd;
        void Save() => _s.SaveLibrary();
        AmdRows.Add(new("Anti-Lag", "Lower input lag in GPU-heavy games.", () => a.AntiLag, v => { a.AntiLag = v; Save(); }));
        AmdRows.Add(new("Radeon Chill", "Lowers FPS when you're not moving to save heat and battery; full FPS when you are.", () => a.Chill, v => { a.Chill = v; Save(); }));
        AmdRows.Add(new("Radeon Super Resolution", "Driver upscaling for games without FSR: run the game at a lower resolution in exclusive fullscreen.", () => a.SuperResolution, v => { a.SuperResolution = v; Save(); }));
        AmdRows.Add(new("Fluid Motion Frames", "Driver frame generation (RDNA 3+): smoother motion, a little more latency. Best above 60 FPS.", () => a.FluidMotionFrames, v => { a.FluidMotionFrames = v; Save(); }));
        AmdRows.Add(new("Image Sharpening", "Sharper image, nearly free; helps when upscaling.", () => a.ImageSharpening, v => { a.ImageSharpening = v; Save(); }));
        AmdRows.Add(new("Radeon Boost", "Drops resolution during fast motion for more FPS.", () => a.Boost, v => { a.Boost = v; Save(); }));
    }

    [RelayCommand]
    private async Task CheckAmdSupportAsync()
    {
        AmdSupportText = "Asking the AMD driver…";
        AmdSupportText = await Task.Run(() =>
        {
            using var amd = AmdFeatures.Open(out var err);
            if (amd is null) return err ?? "AMD's ADLX isn't available.";
            var list = amd.SupportedFeatures();
            return list.Count == 0 ? "The driver reports none of these features as supported on this GPU." : "Supported here: " + string.Join(", ", list) + ".";
        });
    }
}
