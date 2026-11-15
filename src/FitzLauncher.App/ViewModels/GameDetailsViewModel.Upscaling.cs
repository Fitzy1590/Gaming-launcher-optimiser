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

    public static IReadOnlyList<string> OptiScalerModes { get; } = [.. Enum.GetValues<UpscaleMode>().Select(OptiScalerSettings.Label)];
    [ObservableProperty] private int _optiScalerModeIndex;
    [ObservableProperty] private string _optiScalerStatus = "";

    [ObservableProperty] private bool _amdNrVisible;
    [ObservableProperty] private bool _canInstallAmdNr;
    [ObservableProperty] private string _amdNrNote = "";

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
        OptiScalerStatus = BuildOptiScalerStatus();
        var gpu = _s.Hardware?.PrimaryGpu;
        var fit = AmdNrSupport.Check(gpu, layout.HasAntiCheat);
        AmdNrVisible = gpu?.Vendor == Core.Models.GpuVendor.Amd;
        CanInstallAmdNr = fit.CanOffer && !OptiScalerInstalled;
        AmdNrNote = fit.CanOffer && OptiScalerInstalled ? "OptiScaler is already installed in this game. Remove it first; AMD NR replaces it." : fit.Reason;
    }

    private string BuildOptiScalerStatus()
    {
        if (!OptiScalerInstalled) return "";
        var missing = FitzLauncher.Core.Upscalers.OptiScalerStatus.MissingFiles(Game.AppliedChanges, File.Exists);
        var mode = FitzLauncher.Core.Upscalers.OptiScalerStatus.ConfiguredMode(Game.AppliedChanges);
        if (FitzLauncher.Core.Upscalers.OptiScalerStatus.IsAmdNr(Game.AppliedChanges))
            return missing.Count == 0 ? "AMD NR is installed. Remove OptiScaler to take it out again." : $"AMD NR is installed but these files are missing now: {string.Join(", ", missing)}. A game update probably removed them, so remove it and install it again.";
        var line = mode is null ? "Installed." : $"Installed. Set to {mode}; the game's own quality setting can still override this.";
        return missing.Count == 0 ? line : $"{line} Missing now: {string.Join(", ", missing)}. A game update probably removed them, so remove OptiScaler and install it again.";
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
            if (await Task.Run(() => GameModLayout.Detect(GameScanner.ResolveInstallRoot(Game), Game.ExePath).HasAntiCheat))
            { Status = "OptiScaler isn't installed: this game has anti-cheat, which can ban for injected DLLs."; return; }
            var dir = await _s.Upscalers.EnsureOptiScalerAsync();
            var exeDir = Path.GetDirectoryName(Game.LastScan?.RenderExePath ?? Game.ExePath)!;
            var tweak = UpscalerAdvisor.OptiScalerTweak(dir, exeDir, name => File.Exists(Path.Combine(exeDir, name)),
                UpscalerAdvisor.Family(_s.Hardware?.PrimaryGpu), Enum.IsDefined((UpscaleMode)OptiScalerModeIndex) ? (UpscaleMode)OptiScalerModeIndex : UpscaleMode.Quality);
            if (tweak is null) { Status = "The OptiScaler download didn't contain OptiScaler.dll."; return; }
            var r = await Task.Run(() => _s.Tweaks.Apply(Game.AppliedChanges, tweak, Game));
            _s.SaveLibrary();
            Status = r.Success
                ? "OptiScaler installed. In the game choose DLSS (or its FSR/XeSS option), then press Insert in-game to pick XeSS or FSR 3.1 in OptiScaler's menu."
                : $"Couldn't install OptiScaler: {r.Message}";
        }
        catch (Exception e) when (e is Services.Mods.StoreException or System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or SharpCompress.Common.SharpCompressException or TaskCanceledException)
        {
            Status = $"Couldn't get OptiScaler: {e.Message}";
        }
        LoadUpscaling();
    });

    /// <summary>AMD NR replaces stock OptiScaler in a game (never layered on it); removing it uses <see cref="RemoveOptiScalerAsync"/>.</summary>
    [RelayCommand]
    private Task InstallAmdNrAsync() => Busy("Preparing AMD NR…", async () =>
    {
        if (IsPlayingCheck?.Invoke() == true) { Status = "Close the game first."; return; }
        try
        {
            var antiCheat = await Task.Run(() => GameModLayout.Detect(GameScanner.ResolveInstallRoot(Game), Game.ExePath).HasAntiCheat);
            var fit = AmdNrSupport.Check(_s.Hardware?.PrimaryGpu, antiCheat);
            if (!fit.CanOffer) { Status = "AMD NR isn't installed. " + fit.Reason; return; }
            if (FitzLauncher.Core.Upscalers.OptiScalerStatus.IsInstalled(Game.AppliedChanges))
            { Status = "OptiScaler is already installed in this game. Remove it first; AMD NR replaces it rather than sitting on top."; return; }
            var dir = await _s.Upscalers.EnsureAmdNrAsync(new Progress<string>(t => Status = t));
            var exeDir = Path.GetDirectoryName(Game.LastScan?.RenderExePath ?? Game.ExePath)!;
            var tweak = UpscalerAdvisor.OptiScalerTweak(dir, exeDir, name => File.Exists(Path.Combine(exeDir, name)),
                UpscalerAdvisor.Family(_s.Hardware?.PrimaryGpu), null, amdNr: true);
            if (tweak is null) { Status = "The AMD NR download was missing files, so nothing was installed."; return; }
            var r = await Task.Run(() => _s.Tweaks.Apply(Game.AppliedChanges, tweak, Game));
            if (!r.Success)
            {
                // All or nothing: put back whatever was copied before the failure.
                var undo = await Task.Run(() => _s.Tweaks.RevertAll(Game.AppliedChanges, Game, "optiscaler"));
                _s.SaveLibrary();
                Status = $"Couldn't install AMD NR: {r.Message}. " + (undo.Count == 0 ? "Nothing was changed." : "Some files couldn't be put back: " + string.Join("; ", undo));
            }
            else
            {
                _s.SaveLibrary();
                Status = "AMD NR installed. " + fit.Reason + " Start the game, press Insert for the menu, and Home to switch neural rendering on or off.";
            }
        }
        catch (Exception e) when (e is Services.Mods.StoreException or System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or SharpCompress.Common.SharpCompressException or TaskCanceledException)
        {
            Status = $"Couldn't get AMD NR: {e.Message}";
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
