using System.Windows;
using FitzLauncher.App.ViewModels;

namespace FitzLauncher.App.Views;

public partial class ScanFoldersWindow : Window
{
    private readonly ScanFoldersViewModel _vm;

    public ScanFoldersWindow(ScanFoldersViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.RequestClose += Close;
        // Re-scan saved folders straight away so you see what's new.
        Loaded += async (_, _) => { if (vm.HasFolders) await vm.ScanCommand.ExecuteAsync(null); };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
