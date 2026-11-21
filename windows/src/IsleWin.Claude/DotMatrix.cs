using System.Windows;
using System.Windows.Media;
using IsleWin.Core;

namespace IsleWin.Claude;

/// Isle's Claude marker: a 5×5 dot matrix whose pattern, colour and animation say the state
/// (a port of Isle's `DotMatrixView` + `MarkerDesign` defaults). Working is an evolving plasma
/// in the cover's colours, a question is a pulsing blue "?", done a green check, and so on.
public sealed class DotMatrix : FrameworkElement
{
    enum Anim { Solid, Shimmer, Pulse, Blink, Motion, Compact }
    sealed record Design(bool[] Dots, bool Palette, Color Fixed, Anim Animation, double Speed, double Intensity);

    const int Dim = 5, Count = 25;
    const double MorphSeconds = 0.45, PaletteHoldAtFull = 0.30;
    static bool[] Grid(params int[] lit) { var d = new bool[Count]; foreach (var i in lit) d[i] = true; return d; }
    static readonly bool[] Full = Enumerable.Repeat(true, Count).ToArray();
    static readonly bool[] Check = Grid(4, 8, 10, 12, 16), QuestionMark = Grid(1, 2, 3, 8, 12, 22), Cross = Grid(0, 4, 6, 8, 12, 16, 18, 20, 24), MidRow = Grid(10, 12, 14);
    static Color Hex(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    static readonly Color Red = Hex(0xFF3B30), Green = Hex(0x34C759), Blue = Hex(0x0A84FF), Cyan = Hex(0x32ADE6), Gray = Hex(0x8E8E93);

    static Design DesignFor(ClaudeState s) => s switch
    {
        ClaudeState.Disconnected => new(Full, false, Gray, Anim.Pulse, 1.0, 0.18),
        ClaudeState.Idle => new(Full, true, Gray, Anim.Pulse, 1.6, 0.45),
        ClaudeState.Working => new(Full, true, Blue, Anim.Motion, 3.2, 1.0),
        ClaudeState.Done => new(Check, false, Green, Anim.Solid, 2.0, 1.0),
        ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion => new(QuestionMark, false, Blue, Anim.Pulse, 3.5, 1.0),
        ClaudeState.Failed => new(Cross, false, Red, Anim.Pulse, 4.0, 1.0),
        ClaudeState.WaitingInput => new(MidRow, true, Blue, Anim.Shimmer, 1.6, 1.0),
        ClaudeState.Compacting => new(Full, true, Cyan, Anim.Compact, 2.2, 0.9),
        _ => new(Full, false, Gray, Anim.Pulse, 1.0, 0.18),
    };

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(ClaudeState), typeof(DotMatrix),
        new PropertyMetadata(ClaudeState.Disconnected, (d, _) => ((DotMatrix)d).Restyle()));
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(nameof(Primary), typeof(Color), typeof(DotMatrix), new PropertyMetadata(Color.FromRgb(115, 115, 115)));
    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(nameof(Secondary), typeof(Color), typeof(DotMatrix), new PropertyMetadata(Color.FromRgb(77, 77, 77)));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Color), typeof(DotMatrix), new PropertyMetadata(Color.FromRgb(153, 153, 153)));

    public ClaudeState State { get => (ClaudeState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public Color Primary { get => (Color)GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
    public Color Secondary { get => (Color)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    Design _design = DesignFor(ClaudeState.Disconnected);
    readonly double[] _levels = Levels(DesignFor(ClaudeState.Disconnected)), _from = new double[Count], _to = new double[Count];
    double _morphStart = -1;
    static readonly double T0 = Environment.TickCount64 / 1000.0;
    double _lastFrame;

    public DotMatrix()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => { CompositionTarget.Rendering -= OnFrame; _hooked = false; };
    }

    bool _hooked;
    void Hook()
    {
        bool want = IsVisible && IsLoaded;
        if (want == _hooked) return;
        _hooked = want;
        if (want) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? s, EventArgs e)
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastFrame < 1.0 / 30) return; // Isle samples its marker at 30 fps
        _lastFrame = now;
        InvalidateVisual();
    }

    static double[] Levels(Design d) => d.Dots.Select(lit => lit ? 1.0 : 0.08).ToArray(); // unlit dots stay as a faint ghost

    void Restyle()
    {
        var next = DesignFor(State);
        Array.Copy(CurrentLevels(Clock), _from, Count);
        Array.Copy(Levels(next), _to, Count);
        _morphStart = Clock;
        _design = next;
    }

    static double Clock => Environment.TickCount64 / 1000.0 - T0;

    double[] CurrentLevels(double clock)
    {
        if (_morphStart < 0) return _levels;
        double t = Math.Clamp((clock - _morphStart) / MorphSeconds, 0, 1);
        double e = t * t * (3 - 2 * t);
        for (int i = 0; i < Count; i++) _levels[i] = _from[i] + (_to[i] - _from[i]) * e;
        if (t >= 1) _morphStart = -1;
        return _levels;
    }

    double AnimValue(int row, int col, double clock)
    {
        double speed = _design.Speed;
        switch (_design.Animation)
        {
            case Anim.Solid: return 0.82 + 0.18 * (0.5 + 0.5 * Math.Sin(clock * speed * 0.5));
            case Anim.Shimmer: return 0.30 + 0.70 * (0.5 + 0.5 * Math.Sin(clock * speed + row * 0.8 + col * 0.55));
            case Anim.Pulse: return 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(clock * speed));
            case Anim.Blink: return Math.Sin(clock * speed) > 0 ? 1.0 : 0.12;
            case Anim.Motion:
                {
                    double t = clock * speed * 0.32, x = col, y = row, a = t * 0.8;
                    double w1 = Math.Sin((x * Math.Cos(a) + y * Math.Sin(a)) * 1.5 + t * 2.0);
                    double dx = x - 2, dy = y - 2, dist = Math.Sqrt(dx * dx + dy * dy);
                    double w2 = Math.Sin(dist * 1.7 - t * 2.6);
                    double b = -a * 0.6;
                    double w3 = Math.Sin((x * Math.Sin(b) + y * Math.Cos(b)) * 1.1 + t * 1.3);
                    return 0.18 + 0.82 * (0.5 + 0.5 * (w1 + w2 + w3) / 3);
                }
            case Anim.Compact:
                {
                    double frac = Frac(clock * speed * 0.18), tri = Math.Abs(2 * frac - 1);
                    double cutoff = Dim - (1 + tri * (Dim - 1));
                    double tt = Math.Clamp(row - (cutoff - 0.5), 0, 1);
                    return 0.10 + 0.90 * (tt * tt * (3 - 2 * tt));
                }
        }
        return 1;
    }

    static double Frac(double x) => x - Math.Floor(x);
    static Color Lerp(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static Color Lighten(Color c, double k) => Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    Color DotColor(int row, int col, double clock)
    {
        if (!_design.Palette)
        {
            double u = 0.5 + 0.5 * Math.Sin(clock * 0.9 + (row + col) * 0.5);
            return Lerp(_design.Fixed, Lighten(_design.Fixed, 0.35), u * 0.6);
        }
        // a slow ramp primary → accent → secondary drifting across the grid
        double v = Frac(row * 0.19 + col * 0.13 + clock * 0.05) * 3;
        var stops = new[] { Primary, Accent, Secondary };
        int i = (int)Math.Floor(v) % 3;
        return Lerp(stops[i], stops[(i + 1) % 3], v - Math.Floor(v));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double cell = size / Dim, ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        double clock = Clock;
        var levels = CurrentLevels(clock);
        for (int row = 0; row < Dim; row++)
            for (int col = 0; col < Dim; col++)
            {
                double level = levels[row * Dim + col];
                if (level <= 0.001) continue;
                double value = _design.Intensity * level * AnimValue(row, col, clock);
                if (value <= 0.02) continue;
                double hold = _design.Palette ? PaletteHoldAtFull * Math.Min(1, _design.Intensity) * Math.Min(1, level) : 0;
                double floor = 0.10 + hold;
                double alpha = floor + (1 - floor) * Math.Min(1, value);
                double r = cell * 0.5 * (0.28 + 0.55 * Math.Min(1, value)) + 0.5;
                var c = DotColor(row, col, clock);
                var brush = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B));
                brush.Freeze();
                dc.DrawEllipse(brush, null, new Point(ox + (col + 0.5) * cell, oy + (row + 0.5) * cell), r, r);
            }
    }
}
