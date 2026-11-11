using System.Windows;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class ResourceSuggestionsWindow : Window
{
    public ResourceSuggestionsWindow(ResourceSuggestionsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false; };
    }

    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
