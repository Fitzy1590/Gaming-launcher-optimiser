using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.App.ViewModels;

/// <summary>Game page: the best upscaler for this game on this GPU, its DLL versions, and OptiScaler.</summary>
public sealed partial class GameDetailsViewModel
{
    public ObservableCollection<string> UpscalerDllLines { get; } = [];
    [ObservableProperty] private string _upscalerInGame = "";
    [ObservableProperty] private string _upscalerBest = "";
    [ObservableProperty] private string _upscalerWhy = "";
    [ObservableProperty] private string _upscalerFrameGen = "";
    [ObservableProperty] private string _optiScalerWhy = "";
    [ObservableProperty] private bool _suggestOptiScaler;
    [ObservableProperty] private bool _hasUpscalerDlls;

    public bool OptiScalerInstalled => Game.AppliedChanges.Any(c => c.TweakId == "optiscaler");

    private void LoadUpscaling()
    {
        if (Game.LastScan is not { } scan) return;
        var layout = GameModLayout.Detect(GameScanner.ResolveInstallRoot(Game), Game.ExePath);
        var advice = UpscalerAdvisor.Advise(scan, _s.Hardware?.PrimaryGpu, layout);
        UpscalerInGame = advice.InGame;
        UpscalerBest = $"Best for your GPU: {advice.Best}";
        UpscalerWhy = advice.Why + (advice.Notes.Count > 0 ? " " + string.Join(" ", advice.Notes) : "");
        UpscalerFrameGen = advice.FrameGeneration ?? "";
        SuggestOptiScaler = advice.SuggestOptiScaler;
        OptiScalerWhy = advice.OptiScalerWhy ?? "OptiScaler can also swap the upscaler in games that already have one (advanced).";
        var best = _s.BestUpscalerDlls();
        UpscalerDllLines.Clear();
        foreach (var d in scan.UpscalerDlls)
        {
            var newest = best.TryGetValue(d.Kind, out var b) ? b.Version : null;
            var behind = newest is not null && UpscalerLibrary.TryVersion(newest, out var nv) && UpscalerLibrary.TryVersion(d.Version, out var cv) && nv > cv;
            UpscalerDllLines.Add($"{UpscalerLibrary.Label(d.Kind)}: {d.Version}{(behind ? $"  →  {newest} available" : "  (newest)")}");
        }
        HasUpscalerDlls = UpscalerDllLines.Count > 0;
        OnPropertyChanged(nameof(OptiScalerInstalled));
    }

    [RelayCommand]
    private Task UpdateGameDllsAsync() => Busy("Updating this game's upscaler DLLs…", async () =>
    {
        await _s.Upscalers.RefreshLibraryAsync();
        var (_, dlls) = await Task.Run(() => _s.Upscalers.ApplyToLibrary(_ => IsPlayingCheck?.Invoke() == true, [Game]));
        RebuildPlan();
        LoadUpscaling();
        Status = dlls > 0 ? $"Updated {dlls} DLL(s). The originals are backed up (Revert in the suggestions list)." : "This game already has the newest DLLs (or has anti-cheat, so it's left alone).";
    });

    [RelayCommand]
    private Task InstallOptiScalerAsync() => Busy("Downloading OptiScaler…", async () =>
    {
        if (IsPlayingCheck?.Invoke() == true) { Status = "Close the game first."; return; }
        try
        {
            var dir = await _s.Upscalers.EnsureOptiScalerAsync();
            var exeDir = Path.GetDirectoryName(Game.LastScan?.RenderExePath ?? Game.ExePath)!;
            var tweak = UpscalerAdvisor.OptiScalerTweak(dir, exeDir, name => File.Exists(Path.Combine(exeDir, name)));
            if (tweak is null) { Status = "The OptiScaler download didn't contain OptiScaler.dll."; return; }
            var r = await Task.Run(() => _s.Tweaks.Apply(Game.AppliedChanges, tweak, Game));
            _s.SaveLibrary();
            Status = r.Success
                ? "OptiScaler installed. In the game choose DLSS (or its FSR/XeSS option), then press Insert in-game to pick XeSS or FSR 3.1 in OptiScaler's menu."
                : $"Couldn't install OptiScaler: {r.Message}";
        }
        catch (Exception e) when (e is Services.Mods.StoreException or System.Net.Http.HttpRequestException or IOException or InvalidDataException or SharpCompress.Common.SharpCompressException or TaskCanceledException)
        {
            Status = $"Couldn't get OptiScaler: {e.Message}";
        }
        LoadUpscaling();
    });

    [RelayCommand]
    private Task RemoveOptiScalerAsync() => Busy("Removing OptiScaler…", async () =>
    {
        var errors = await Task.Run(() => _s.Tweaks.RevertAll(Game.AppliedChanges, Game, "optiscaler"));
        _s.SaveLibrary();
        Status = errors.Count == 0 ? "OptiScaler removed; the game's own files are back." : "Some files couldn't be restored: " + string.Join("; ", errors);
        LoadUpscaling();
    });
}
