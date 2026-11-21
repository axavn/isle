using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IsleWin.Claude;

namespace IsleWin;

/// Isle's MarqueeText: text that fits sits still; text that doesn't scrolls left forever at 30 pt/s
/// with a 44 pt gap between passes, after a 0.3 s hold, its edges faded over 4%.
public sealed class Marquee : Grid
{
    const double Speed = 30, Gap = 44, StartDelay = 0.3;
    readonly StackPanel _track = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _a, _b;
    readonly TranslateTransform _shift = new();
    string _text = "";

    public Marquee(double fontSize, FontWeight weight, Brush color, double lineHeight)
    {
        ClipToBounds = true; Height = lineHeight;
        _a = Make(fontSize, weight, color); _b = Make(fontSize, weight, color);
        _b.Margin = new Thickness(Gap, 0, 0, 0);
        _track.Children.Add(_a); _track.Children.Add(_b);
        _track.RenderTransform = _shift;
        _track.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(new Canvas { Children = { _track } }); // canvas: the track keeps its natural width
        SizeChanged += (_, _) => Restart();
    }

    static TextBlock Make(double size, FontWeight weight, Brush color) =>
        new() { FontFamily = IsleStyle.Text, FontSize = size, FontWeight = weight, Foreground = color, VerticalAlignment = VerticalAlignment.Center };

    public string Text
    {
        get => _text;
        set { if (value == _text) return; _text = value; _a.Text = _b.Text = value; Restart(); }
    }

    void Restart()
    {
        _shift.BeginAnimation(TranslateTransform.XProperty, null); _shift.X = 0;
        _a.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _a.DesiredSize.Width;
        bool scroll = ActualWidth > 0 && tw > ActualWidth + 0.5;
        _b.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetTop(_track, (Height - _a.DesiredSize.Height) / 2);
        if (!scroll) { OpacityMask = null; return; }
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
            GradientStops = { new(Colors.Transparent, 0), new(Colors.Black, 0.04), new(Colors.Black, 0.96), new(Colors.Transparent, 1) },
        };
        double distance = tw + Gap;
        _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -distance, TimeSpan.FromSeconds(distance / Speed))
        { BeginTime = TimeSpan.FromSeconds(StartDelay), RepeatBehavior = RepeatBehavior.Forever });
    }
}
