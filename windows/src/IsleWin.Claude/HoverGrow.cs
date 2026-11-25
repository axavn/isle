using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IsleWin.Claude;

/// Isle's hover hint (HoverGrow.swift, 1.3.2): a control on the open island grows a little while the
/// pointer is on it, so it's plain what a click there would press. The same few points for every control
/// (2 pt a side, at most 1.15×), on a quick spring (response 0.22, damping 0.7), settling the moment the
/// pointer leaves. With `press`, a press also dips it (0.92), as the transport keys always have.
/// In XAML: `<Setter Property="local:HoverGrow.Enabled" Value="True"/>`; in code: `HoverGrow.Attach(e)`.
public static class HoverGrow
{
    const double Reach = 2, MaxScale = 1.15, Pressed = 0.92;
    static readonly Duration Time = TimeSpan.FromSeconds(0.22);
    static readonly IEasingFunction Spring = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }; // damping 0.7's small overshoot

    public static double ScaleFor(Size s)
    {
        double side = Math.Max(s.Width, s.Height);
        return side <= 0 ? 1 : Math.Min(MaxScale, 1 + 2 * Reach / side);
    }

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(HoverGrow), new PropertyMetadata(false, (d, e) => { if (d is FrameworkElement f && (bool)e.NewValue) Attach(f); }));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    public static void Attach(FrameworkElement e, bool press = false)
    {
        var scale = new ScaleTransform(1, 1);
        e.RenderTransform = scale; e.RenderTransformOrigin = new Point(0.5, 0.5);
        bool over = false, down = false;
        void Go()
        {
            double to = (over && e.IsEnabled ? ScaleFor(e.RenderSize) : 1) * (down ? Pressed : 1);
            var a = new DoubleAnimation(to, Time) { EasingFunction = Spring };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        e.MouseEnter += (_, _) => { over = true; Go(); };
        e.MouseLeave += (_, _) => { over = false; down = false; Go(); };
        if (press)
        {
            e.PreviewMouseLeftButtonDown += (_, _) => { down = true; Go(); };
            e.PreviewMouseLeftButtonUp += (_, _) => { down = false; Go(); };
        }
    }
}
