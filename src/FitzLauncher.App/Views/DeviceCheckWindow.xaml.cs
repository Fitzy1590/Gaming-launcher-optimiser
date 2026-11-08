using System.Windows;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class DeviceCheckWindow : Window
{
    public DeviceCheckWindow(DeviceCheckViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
