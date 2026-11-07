using System.Windows;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;
using Microsoft.Win32;

namespace FitzLauncher.App.Views;

public partial class EditGameWindow : Window
{
    private readonly Game _game;
    private string? _artPath;
    private bool _refetch;

    public EditGameWindow(Game game)
    {
        InitializeComponent();
        _game = game;
        NameBox.Text = game.Name;
        ExeBox.Text = game.ExePath;
        ArgsBox.Text = game.Arguments;
        ConfigBox.Text = game.CustomConfigDir ?? "";
        GameModeBox.IsChecked = game.UseGameMode;
        FavouriteBox.IsChecked = game.Favourite;
        OptimizerArgs.Text = game.OptimizerArguments.Length > 0 ? $"Added by optimisation: {game.OptimizerArguments}" : "";
        _artPath = game.ArtworkPath;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe", FileName = ExeBox.Text };
        if (dlg.ShowDialog(this) == true) ExeBox.Text = dlg.FileName;
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder with the game's config files" };
        if (dlg.ShowDialog(this) == true) ConfigBox.Text = dlg.FolderName;
    }

    private void ChooseArt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        // Copy into the launcher's artwork folder so the library doesn't depend on the original file.
        var dest = Path.Combine(AppPaths.Default().ArtworkDir, $"custom_{_game.Id:N}_{DateTime.Now:HHmmss}{Path.GetExtension(dlg.FileName)}");
        File.Copy(dlg.FileName, dest, overwrite: true);
        _artPath = dest;
        _refetch = false;
        ArtStatus.Text = Path.GetFileName(dlg.FileName);
    }

    private void Refetch_Click(object sender, RoutedEventArgs e)
    {
        _refetch = true;
        ArtStatus.Text = "Will search again when saved";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ExeBox.Text))
        {
            MessageBox.Show(this, "That executable doesn't exist.", "Edit game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var exeChanged = !ExeBox.Text.Equals(_game.ExePath, StringComparison.OrdinalIgnoreCase);
        _game.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _game.Name : NameBox.Text.Trim();
        _game.ExePath = ExeBox.Text;
        if (exeChanged)
        {
            _game.InstallDir = GameScanner.GuessRootFromExe(Path.GetDirectoryName(ExeBox.Text)!);
            _game.LastScan = null;
        }
        _game.Arguments = ArgsBox.Text.Trim();
        var newConfig = string.IsNullOrWhiteSpace(ConfigBox.Text) ? null : ConfigBox.Text.Trim();
        if (newConfig != _game.CustomConfigDir) _game.LastScan = null;
        _game.CustomConfigDir = newConfig;
        _game.UseGameMode = GameModeBox.IsChecked == true;
        _game.Favourite = FavouriteBox.IsChecked == true;
        if (_refetch)
        {
            _game.ArtworkPath = null;
            _game.HeroPath = null;
        }
        else _game.ArtworkPath = _artPath;
        DialogResult = true;
    }
}
