using System.Windows;
using System.Windows.Media;

namespace IsleWin;

/// Isle's PomodoroRing: a track at 22% tint and a round-capped arc from 12 o'clock, clockwise, inset by half the
/// stroke so it stays inside its frame. Sizes: 114 (stroke 6, the face), 22 (2.5, the lone pill), 16 (2, beside Claude).
public sealed class PomodoroRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PomodoroRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(PomodoroRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineWidthProperty = DependencyProperty.Register(nameof(LineWidth), typeof(double), typeof(PomodoroRing),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double LineWidth { get => (double)GetValue(LineWidthProperty); set => SetValue(LineWidthProperty, value); }

    public static PomodoroRing Hero() => new() { Width = 114, Height = 114, LineWidth = 6 };
    public static PomodoroRing Alone() => new() { Width = 22, Height = 22, LineWidth = 2.5 };
    public static PomodoroRing Small() => new() { Width = 16, Height = 16, LineWidth = 2 };

    protected override void OnRender(DrawingContext dc)
    {
        double lw = LineWidth, d = Math.Min(ActualWidth, ActualHeight) - lw;
        if (d <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = d / 2;

        var track = Tint.CloneCurrentValue();
        track.Opacity *= 0.22;
        dc.DrawEllipse(null, new Pen(track, lw), c, r, r);

        double p = Math.Clamp(Progress, 0, 1);
        if (p <= 0) return;
        var pen = new Pen(Tint, lw) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.9999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = 2 * Math.PI * p - Math.PI / 2;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var fig = new PathFigure(start, [new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true)], false);
        dc.DrawGeometry(null, pen, new PathGeometry([fig]));
    }
}
