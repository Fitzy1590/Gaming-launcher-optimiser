using System.Windows;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class ModsWindow : Window, FitzLauncher.App.Services.IGamepadAware
{
    public ModsWindow(ModsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }

    private static readonly string[] Tabs = ["Installed", "Find", "Check", "Troubleshoot"];

    /// <summary>LB/RB switch tabs; Start runs the main action of the tab.</summary>
    public bool OnGamepad(FitzLauncher.App.Services.PadButton b)
    {
        if (DataContext is not ModsViewModel vm) return false;
        if (b is FitzLauncher.App.Services.PadButton.LeftShoulder or FitzLauncher.App.Services.PadButton.RightShoulder)
        {
            var i = Array.IndexOf(Tabs, vm.Tab);
            vm.Tab = Tabs[(i + (b == FitzLauncher.App.Services.PadButton.RightShoulder ? 1 : Tabs.Length - 1)) % Tabs.Length];
            Dispatcher.BeginInvoke(() => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Loaded);
            return true;
        }
        if (b == FitzLauncher.App.Services.PadButton.Start)
        {
            (vm.Tab switch { "Find" => vm.SearchCommand, "Check" => vm.CheckCommand, "Troubleshoot" => (System.Windows.Input.ICommand)vm.AnalyseCommand, _ => vm.CheckCommand }).Execute(null);
            return true;
        }
        return false;
    }
}
