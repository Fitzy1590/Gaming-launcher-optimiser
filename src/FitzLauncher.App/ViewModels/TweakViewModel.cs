using CommunityToolkit.Mvvm.ComponentModel;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

public sealed partial class TweakViewModel(Tweak tweak, Func<TweakViewModel, Task> apply, Func<TweakViewModel, Task> revert) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;

    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string? _message;

    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Gain => Tweak.ExpectedGain;
    public bool HasGain => Tweak.ExpectedGain.Length > 0;
    public bool IsAutomatic => Tweak.IsAutomatic;
    public bool IsManual => !Tweak.IsAutomatic;
    public bool Recommended => Tweak.Recommended;
    public string? Reason => Tweak.Reason;
    public bool HasReason => !string.IsNullOrEmpty(Tweak.Reason);
    public string ManualSteps => string.Join("\n", Tweak.Actions.OfType<ManualStepAction>().Select(a => "→ " + a.Instructions));
    public string Details => string.Join("\n", Tweak.Actions.Where(a => a is not ManualStepAction).Select(a => a.Describe()));

    public string ImpactText => Tweak.VisualImpact switch
    {
        VisualImpact.None => "No visual change",
        VisualImpact.Low => "Barely visible",
        VisualImpact.Medium => "Some visual change",
        _ => "Visible quality loss",
    };

    public string ImpactLevel => Tweak.VisualImpact.ToString();

    public string RiskText => Tweak.Risk switch
    {
        TweakRisk.Safe => "",
        TweakRisk.Moderate => "Test it",
        _ => "Experimental",
    };
    public bool HasRisk => Tweak.Risk != TweakRisk.Safe;
    public bool NeedsRestart => Tweak.NeedsRestart;

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand ApplyCommand => _applyCommand ??= new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => apply(this));
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand RevertCommand => _revertCommand ??= new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => revert(this));
    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? _applyCommand, _revertCommand;
}
