using System.Windows;
using System.Windows.Media;
using IsleWin.Media;

namespace IsleWin;

/// Isle's equalizer (a port of `EqualizerLayer`): 6 bars, 2.5 wide, growing out from the middle,
/// one vertical gradient primary (top) → accent (bottom) behind all of them. Paused, the bars rest
/// as 2.5-high dots. Fed 16 analysis bands; each bar shows the loudest band in its slice.
public sealed class Equalizer : FrameworkElement
{
    const int Bars = 6;
    const double Thickness = 2.5, DotHeight = 2.5, MinSpacing = 1.2;
    readonly float[] _levels = new float[Bars];
    LinearGradientBrush _brush = Build(ArtworkPalette.Fallback);
    ArtworkPalette _palette = ArtworkPalette.Fallback;

    public Equalizer() { IsHitTestVisible = false; SnapsToDevicePixels = false; }

    static LinearGradientBrush Build(ArtworkPalette p)
    {
        var b = new LinearGradientBrush(p.Primary, p.Accent, new Point(0.5, 0), new Point(0.5, 1));
        b.Freeze(); return b;
    }

    public void SetPalette(ArtworkPalette p)
    {
        if (p == _palette) return;
        _palette = p; _brush = Build(p); InvalidateVisual();
    }

    /// null = resting dots.
    public void SetLevels(float[]? v)
    {
        for (int i = 0; i < Bars; i++)
        {
            float x = 0;
            if (v is { Length: > 0 })
            {
                int a = i * v.Length / Bars, b = Math.Max(a + 1, (i + 1) * v.Length / Bars);
                for (int k = a; k < b && k < v.Length; k++) x = Math.Max(x, v[k]);
            }
            _levels[i] = x;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double spacing = (w - Thickness * Bars) / (Bars - 1), bw = Thickness;
        if (spacing < MinSpacing) { spacing = MinSpacing; bw = Math.Max(1, (w - MinSpacing * (Bars - 1)) / Bars); }
        // the gradient spans the whole box, so a short bar shows the middle of it, like the masked layer on macOS
        var brush = new LinearGradientBrush(_brush.GradientStops, new Point(0, 0), new Point(0, h)) { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze();
        for (int i = 0; i < Bars; i++)
        {
            double bh = Math.Max(DotHeight, _levels[i] * h);
            double x = i * (bw + spacing), y = (h - bh) / 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, bw, bh), bw / 2, bw / 2);
        }
    }
}
