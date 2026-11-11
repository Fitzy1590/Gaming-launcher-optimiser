using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

/// <summary>One app offered before launch: what to do with it now, and whether to remember that.</summary>
public sealed partial class ResourceChoiceRow : ObservableObject
{
    public static readonly string[] NowOptions = ["Leave it running", "Pause it until the game ends", "Close it"];
    public static readonly string[] NextOptions = ["Ask me again next time", "Remember my choice", "Never touch this app"];

    public ResourceSuggestion Suggestion { get; }
    public string Name => Suggestion.App.Name;
    public string Why => Suggestion.Why;
    public string? History => Suggestion.HistoryNote;
    public bool HasHistory => History is not null;
    public bool CanClose => Suggestion.Options.Contains(FreeAction.Close);

    [ObservableProperty] private int _now;
    [ObservableProperty] private int _next;

    public ResourceChoiceRow(ResourceSuggestion s)
    {
        Suggestion = s;
        _now = s.Default switch { FreeAction.Pause => 1, FreeAction.Close => 2, _ => 0 };
    }

    public FreeAction? Action => Now switch { 1 => FreeAction.Pause, 2 when CanClose => FreeAction.Close, _ => null };
}

/// <summary>The "free up resources before this game starts" prompt.</summary>
public sealed partial class ResourceSuggestionsViewModel : ObservableObject
{
    public string GameName { get; }
    public ObservableCollection<ResourceChoiceRow> Rows { get; }
    public IReadOnlyList<PlannedAction> Automatic { get; }
    public string AutomaticText { get; }
    public bool HasAutomatic => Automatic.Count > 0;

    public ResourceSuggestionsViewModel(string gameName, LaunchPlan plan)
    {
        GameName = gameName;
        Automatic = plan.Automatic;
        Rows = [.. plan.Suggestions.Select(s => new ResourceChoiceRow(s))];
        AutomaticText = Automatic.Count == 0 ? "" : "Already being done safely (undone when the game ends): " +
            string.Join("; ", Automatic.Select(a => a.Reason));
    }

    /// <summary>Saves the choices and returns the steps to take.</summary>
    public List<PlannedAction> Commit(ResourceMemory memory, DateTimeOffset now)
    {
        var chosen = new List<PlannedAction>();
        foreach (var row in Rows)
        {
            var action = row.Action;
            switch (row.Next)
            {
                case 2: memory.Remember(row.Name, ResourceChoice.Never, null, now); continue;
                case 1: memory.Remember(row.Name, action is null ? ResourceChoice.Never : ResourceChoice.Always, action, now); break;
            }
            if (action is { } a) chosen.Add(new PlannedAction(row.Suggestion.App, a, $"{ResourcePlanner.Verb(a)} {row.Name}"));
        }
        return chosen;
    }
}

/// <summary>A saved choice shown in Settings, with a way to forget it.</summary>
public sealed partial class ResourceRuleRow(ResourceRule rule, Action<ResourceRule> forget) : ObservableObject
{
    public string Name => rule.Name;
    public string Text => rule.Choice == ResourceChoice.Never ? "Never touched" : $"Always: {ResourcePlanner.Verb(rule.Action ?? FreeAction.Pause).ToLowerInvariant()} it";
    [RelayCommand] private void Forget() => forget(rule);
}

public sealed class ResourceHistoryRow(ResourceOutcome o)
{
    public string Name => o.Name;
    public string Text => $"{ResourcePlanner.Verb(o.Action)}: helped {o.Helped} of {o.Tries} tries ({ResourcePlanner.Gain(o)})" +
        (o.Hurt > 0 ? $"; made things worse {o.Hurt}×" : "") + (o.Hurt >= 2 && o.Helped == 0 ? ". No longer tried on its own." : "");
}
