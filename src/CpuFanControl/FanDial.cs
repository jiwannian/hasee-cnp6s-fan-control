using System.Windows;
using System.Windows.Media;

namespace CpuFanControl;

public sealed class FanDial : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent),
        typeof(int), typeof(FanDial), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int Percent { get => (int)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 7);
        var track = (Brush)FindResource("Line");
        var accent = (Brush)FindResource("Accent");
        dc.DrawEllipse(null, new Pen(track, 8), center, radius, radius);
        int value = Math.Clamp(Percent, 0, 100);
        if (value == 100) dc.DrawEllipse(null, new Pen(accent, 8), center, radius, radius);
        else if (value > 0)
        {
            double angle = value / 100.0 * Math.PI * 2 - Math.PI / 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                context.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                    new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
    }
}
