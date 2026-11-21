using System.Windows;
using System.Windows.Media;

namespace IsleWin;

/// A damped spring with SwiftUI's parameterisation (response, dampingFraction), stepped per frame.
/// Retargeting keeps the current value and velocity, so an interrupted open/close just bends.
public sealed class Spring
{
    public double Value, Velocity, Target;
    double _k, _c;
    public Spring(double value) { Value = Target = value; Use(0.34, 0.76); }

    public void Use(double response, double damping)
    {
        _k = Math.Pow(2 * Math.PI / response, 2);
        _c = 2 * damping * Math.Sqrt(_k);
    }

    public void Step(double dt)
    {
        // semi-implicit Euler in small slices: stable for these stiffnesses
        int n = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 480)));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double a = -_k * (Value - Target) - _c * Velocity;
            Velocity += a * h; Value += Velocity * h;
        }
    }

    public bool Settled => Math.Abs(Value - Target) < 0.01 && Math.Abs(Velocity) < 0.05;
    public void Snap(double v) { Value = Target = v; Velocity = 0; }
}

/// Isle's motion constants (IslandMotion.swift) and the island outline (NotchShape.swift).
public static class IslandMotion
{
    public static readonly (double R, double D) Open = (0.34, 0.76);
    public static readonly (double R, double D) CollapseWidth = (0.24, 0.78);
    public static readonly (double R, double D) CollapseHeight = (0.30, 1.0);
    public static readonly (double R, double D) CollapseCorners = (0.18, 1.0);
    public static readonly (double R, double D) Swell = (0.24, 0.86);

    public static readonly (double Top, double Bottom) OpenCorners = (12, 22);
    public static readonly (double Top, double Bottom) CollapsedCorners = (8, 12);

    const double MaxTaper = 14, TaperGain = 36, TaperRamp = 20;

    // proximity swell: the collapsed island grows a touch and leans toward a nearby pointer
    public const double ProximityReach = 56, MaxSwellW = 3, MaxSwellH = 2, MaxLean = 1.5;
    public const int ProximitySteps = 8, LeanSteps = 3;

    /// How far a collapsing island's walls pinch in: the width leads the height, and the
    /// difference reads as the panel being drawn back up into the pill.
    public static double Taper(Size rect, Size expanded, Size collapsed)
    {
        double spanW = expanded.Width - collapsed.Width, spanH = expanded.Height - collapsed.Height;
        if (spanW <= 1 || spanH <= 1) return 0;
        double wp = Math.Clamp((expanded.Width - rect.Width) / spanW, 0, 1);
        double hp = Math.Clamp((expanded.Height - rect.Height) / spanH, 0, 1);
        return Math.Min(Math.Max(0, wp - hp) * TaperGain, MaxTaper);
    }

    /// The notch outline: a flat top edge that flares down into the walls (top radius, concave),
    /// rounded bottom corners, and an optional taper pinching the lower walls in.
    public static Geometry Outline(double w, double h, double topR, double bottomR, double pinchIn)
    {
        double maxR = Math.Min(w, h) / 2;
        double top = Math.Clamp(topR, 0, maxR), bottom = Math.Min(bottomR, maxR);
        double wallTop = top, wallBottom = h - bottom;
        double pinch = Math.Min(pinchIn, Math.Max(0, (w - 2 * (top + bottom)) / 2));
        double room = wallBottom - wallTop;
        double taper = room > 0 ? pinch * Math.Min(1, room / TaperRamp) : 0;
        double ease = room * 0.45;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.QuadraticBezierTo(new Point(top, 0), new Point(top, wallTop), true, true);
            if (taper > 0) c.BezierTo(new Point(top, wallTop + ease), new Point(top + taper, wallBottom - ease), new Point(top + taper, wallBottom), true, true);
            else c.LineTo(new Point(top, wallBottom), true, true);
            c.QuadraticBezierTo(new Point(top + taper, h), new Point(top + taper + bottom, h), true, true);
            c.LineTo(new Point(w - top - taper - bottom, h), true, true);
            c.QuadraticBezierTo(new Point(w - top - taper, h), new Point(w - top - taper, wallBottom), true, true);
            if (taper > 0) c.BezierTo(new Point(w - top - taper, wallBottom - ease), new Point(w - top, wallTop + ease), new Point(w - top, wallTop), true, true);
            else c.LineTo(new Point(w - top, wallTop), true, true);
            c.QuadraticBezierTo(new Point(w - top, 0), new Point(w, 0), true, true);
        }
        g.Freeze();
        return g;
    }
}
