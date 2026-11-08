using System.Windows;
using System.Windows.Controls;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

/// <summary>Controller profile editor; the live preview only polls while it's on screen.</summary>
public partial class ControllerProfileEditor : UserControl
{
    public ControllerProfileEditor() => InitializeComponent();

    private ControllerProfileViewModel? Vm => DataContext as ControllerProfileViewModel;
    private void OnLoaded(object sender, RoutedEventArgs e) { if (IsVisible) Vm?.StartPreview(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Vm?.StopPreview();
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Vm?.StartPreview(); else Vm?.StopPreview();
    }
}
