using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Diagnostics;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

public sealed partial class DeviceCheckRow(DeviceCheckItem item, DeviceCheckViewModel owner) : ObservableObject
{
    public DeviceCheckItem Item { get; } = item;
    public string Name => Item.Name;
    public string Detail => Item.Detail;
    public string Status => Item.Status.ToString();
    public string StatusLabel => Item.Status switch { CheckStatus.Ok => "OK", CheckStatus.Required => "Needed", CheckStatus.Recommended => "Recommended", _ => "Optional" };
    public bool HasFix => Item.Fix is not null && Item.Status != CheckStatus.Ok;
    public string FixLabel => Item.Fix?.Label ?? "";
    public bool CanInstall => Item.InstallerUrl is not null && Item.Status != CheckStatus.Ok;
    [RelayCommand] private void OpenFix() { if (Item.Fix is { } f) DeviceCheckViewModel.Open(f.Url); }
    [RelayCommand] private Task InstallAsync() => owner.InstallAsync(this);
}

/// <summary>The device check: what's installed, what's missing, and one-click fixes where Microsoft offers an installer.</summary>
public sealed partial class DeviceCheckViewModel(AppServices s) : ObservableObject
{
    public ObservableCollection<DeviceCheckRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "Checking this PC…";
    [ObservableProperty] private bool _isBusy;

    public bool NeedsAttention => Rows.Any(r => r.Item.NeedsAction);

    [RelayCommand]
    public async Task RunAsync()
    {
        IsBusy = true;
        var items = await Task.Run(() => DeviceChecker.Run(s.Paths, s.Hardware?.IsApu == true, s.Hardware?.RtssPath));
        Rows.Clear();
        foreach (var i in items.OrderByDescending(i => i.Status == CheckStatus.Required).ThenByDescending(i => i.Status == CheckStatus.Recommended).ThenBy(i => i.Status == CheckStatus.Info))
            Rows.Add(new DeviceCheckRow(i, this));
        var required = items.Count(i => i.Status == CheckStatus.Required);
        var recommended = items.Count(i => i.Status == CheckStatus.Recommended);
        Summary = required > 0 ? $"{required} thing(s) must be fixed for the launcher to work properly, plus {recommended} recommended."
            : recommended > 0 ? $"Everything the launcher needs is here. {recommended} recommended item(s) help games and tools run."
            : "Everything is in place.";
        s.Settings.DeviceCheckedVersion = UpdateService.CurrentVersion.ToString();
        s.SaveSettings();
        OnPropertyChanged(nameof(NeedsAttention));
        IsBusy = false;
    }

    /// <summary>Downloads Microsoft's installer and runs it silently, then re-checks.</summary>
    public async Task InstallAsync(DeviceCheckRow row)
    {
        if (row.Item.InstallerUrl is not { } url) return;
        IsBusy = true;
        Summary = $"Downloading {row.Name} from Microsoft…";
        try
        {
            var file = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).AbsolutePath));
            using (var res = await s.StoreHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                await src.CopyToAsync(dst);
            }
            Summary = $"Installing {row.Name}…";
            using var p = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true })!;
            await p.WaitForExitAsync();
            Summary = p.ExitCode is 0 or 1638 or 3010 ? $"{row.Name} installed." : $"The installer exited with code {p.ExitCode}. Use the download link to install it by hand.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            Summary = $"Couldn't install {row.Name}: {e.Message}. Use the download link instead.";
        }
        IsBusy = false;
        var keep = Summary;
        await RunAsync();
        Summary = keep + " " + Summary;
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
