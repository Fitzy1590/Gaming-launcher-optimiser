using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FitzLauncher.App.Controls;

/// <summary>
/// Circular progress (0 to Maximum). Draws a track in <c>Background</c> and the filled arc in <c>Foreground</c>;
/// the template in Theme.xaml puts <see cref="Label"/> in the middle. Display only, so it never takes focus.
/// </summary>
public sealed class ProgressRing : RangeBase
{
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ProgressRing), new PropertyMetadata(""));

    static ProgressRing()
    {
        MaximumProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(100.0));
        FocusableProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(false));
    }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty || e.Property == BackgroundProperty || e.Property == MinimumProperty || e.Property == MaximumProperty)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var t = Math.Min(Thickness, size / 2);
        if (size <= 0 || t <= 0) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size - t) / 2;
        dc.DrawEllipse(null, new Pen(Background, t), centre, r, r);

        var range = Maximum - Minimum;
        var fraction = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        if (fraction <= 0) return;
        var arcPen = new Pen(Foreground, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 0.9999)
        {
            dc.DrawEllipse(null, arcPen, centre, r, r);
            return;
        }

        // Clockwise from 12 o'clock.
        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - r);
        var end = new Point(centre.X + r * Math.Sin(angle), centre.Y - r * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(r, r), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, arcPen, geometry);
    }
}
