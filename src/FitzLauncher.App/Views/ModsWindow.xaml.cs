using System.Windows;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class ModsWindow : Window
{
    public ModsWindow(ModsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
