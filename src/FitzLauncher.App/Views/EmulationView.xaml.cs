using System.Windows;
using System.Windows.Controls;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class EmulationView : UserControl
{
    public EmulationView() => InitializeComponent();

    // Tab switch only: no logic, just view state.
    private void ShowGames_Click(object sender, RoutedEventArgs e) { if (DataContext is EmulationViewModel vm) vm.ShowEmulators = false; }
    private void ShowEmulators_Click(object sender, RoutedEventArgs e) { if (DataContext is EmulationViewModel vm) vm.ShowEmulators = true; }
}
