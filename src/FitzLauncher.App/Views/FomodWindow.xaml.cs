using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FitzLauncher.Core.Mods;

namespace FitzLauncher.App.Views;

/// <summary>Lets you pick a FOMOD installer's options; one-choice groups are radio buttons, others checkboxes.</summary>
public partial class FomodWindow : Window
{
    private readonly List<(FomodGroup group, List<ToggleButton> boxes)> _rows = [];

    /// <summary>The picks, or null to use the author's defaults.</summary>
    public Dictionary<string, HashSet<string>>? Choices { get; private set; }

    public FomodWindow(string modName, IReadOnlyList<FomodGroup> groups)
    {
        InitializeComponent();
        TitleText.Text = modName;
        foreach (var step in groups.GroupBy(g => g.Step))
        {
            if (!string.IsNullOrWhiteSpace(step.Key) && groups.Select(g => g.Step).Distinct().Count() > 1)
                GroupsPanel.Children.Add(new TextBlock { Text = step.Key, Style = (Style)FindResource("H2") });
            foreach (var g in step)
            {
                var card = new Border { Style = (Style)FindResource("Card") };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = g.Name + (g.SingleChoice ? "  ·  pick one" : g.Type == "SelectAtLeastOne" ? "  ·  pick at least one" : "  ·  pick any"),
                    FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("Text"), Margin = new Thickness(0, 0, 0, 8),
                });
                var boxes = new List<ToggleButton>();
                foreach (var o in g.Options)
                {
                    ToggleButton box = g.SingleChoice ? new RadioButton { GroupName = g.Key } : new CheckBox();
                    box.IsChecked = o.IsDefault;
                    box.IsEnabled = o.Kind is not ("NotUsable" or "Required");
                    if (o.Kind == "Required") box.IsChecked = true;
                    box.Tag = o.Name;
                    box.Content = new TextBlock
                    {
                        Text = o.Name + (o.Kind == "Recommended" ? "  (recommended)" : o.Kind == "NotUsable" ? "  (not available)" : ""),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    box.Margin = new Thickness(0, 0, 0, 4);
                    if (o.Description.Length > 0) box.ToolTip = o.Description;
                    panel.Children.Add(box);
                    if (o.Description.Length > 0)
                        panel.Children.Add(new TextBlock { Text = o.Description, Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 8), MaxHeight = 60 });
                    boxes.Add(box);
                }
                card.Child = panel;
                GroupsPanel.Children.Add(card);
                _rows.Add((g, boxes));
            }
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (g, boxes) in _rows.Where(r => r.group.Type == "SelectAtLeastOne"))
            if (!boxes.Any(b => b.IsChecked == true))
            {
                MessageBox.Show(this, $"\"{g.Name}\" needs at least one option.", "Fitz Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        Choices = _rows.ToDictionary(r => r.group.Key, r => r.boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToHashSet());
        DialogResult = true;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Choices = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
