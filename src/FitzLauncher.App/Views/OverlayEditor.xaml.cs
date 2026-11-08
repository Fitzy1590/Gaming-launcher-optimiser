using System.Windows.Controls;

namespace FitzLauncher.App.Views;

/// <summary>Overlay layout editor (Settings default and per game).</summary>
public partial class OverlayEditor : UserControl
{
    public OverlayEditor() => InitializeComponent();

    /// <summary>Keeps radio groups separate when two editors exist.</summary>
    public string GroupId { get; } = "overlay-" + Guid.NewGuid().ToString("N");
}
