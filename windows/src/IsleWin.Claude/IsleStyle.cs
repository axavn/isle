using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IsleWin.Claude;

/// Isle's (macOS) type scale, tones and icons, mapped to Windows.
/// SF Pro → Segoe UI Variable (the closest system face); SF Symbols → hand-drawn paths shaped like
/// the symbols where it matters (transport, grid), Segoe Fluent Icons glyphs for the rest.
public static class IsleStyle
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // IsleType.Size
    public const double Title = 17, Lead = 15, Headline = 13, Body = 12, Meta = 11, Caption = 10, Micro = 9;
    // IsleType.Tone (white at these opacities)
    public const double Secondary = 0.7, MetaTone = 0.5, Faint = 0.35;

    public static Brush White(double opacity) { var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255)); b.Freeze(); return b; }

    // SF Symbol look-alikes, drawn in a 24×24 box
    public const string PlayFill = "M7,4.6 C7,3.8 7.9,3.3 8.6,3.7 L19.7,10.8 C20.3,11.2 20.3,12.8 19.7,13.2 L8.6,20.3 C7.9,20.7 7,20.2 7,19.4 Z";
    public const string PauseFill = "M6.2,3.5 H9.3 A1.2,1.2 0 0 1 10.5,4.7 V19.3 A1.2,1.2 0 0 1 9.3,20.5 H6.2 A1.2,1.2 0 0 1 5,19.3 V4.7 A1.2,1.2 0 0 1 6.2,3.5 Z M14.7,3.5 H17.8 A1.2,1.2 0 0 1 19,4.7 V19.3 A1.2,1.2 0 0 1 17.8,20.5 H14.7 A1.2,1.2 0 0 1 13.5,19.3 V4.7 A1.2,1.2 0 0 1 14.7,3.5 Z";
    public const string ForwardFill = "M1.5,6.6 C1.5,5.8 2.4,5.3 3.1,5.8 L11.3,11.1 C11.9,11.5 11.9,12.5 11.3,12.9 L3.1,18.2 C2.4,18.7 1.5,18.2 1.5,17.4 Z M12.2,6.6 C12.2,5.8 13.1,5.3 13.8,5.8 L22,11.1 C22.6,11.5 22.6,12.5 22,12.9 L13.8,18.2 C13.1,18.7 12.2,18.2 12.2,17.4 Z";
    public const string BackwardFill = "M22.5,6.6 C22.5,5.8 21.6,5.3 20.9,5.8 L12.7,11.1 C12.1,11.5 12.1,12.5 12.7,12.9 L20.9,18.2 C21.6,18.7 22.5,18.2 22.5,17.4 Z M11.8,6.6 C11.8,5.8 10.9,5.3 10.2,5.8 L2,11.1 C1.4,11.5 1.4,12.5 2,12.9 L10.2,18.2 C10.9,18.7 11.8,18.2 11.8,17.4 Z";
    public const string Grid2x2Fill = "M3,5 A2,2 0 0 1 5,3 H9 A2,2 0 0 1 11,5 V9 A2,2 0 0 1 9,11 H5 A2,2 0 0 1 3,9 Z M13,5 A2,2 0 0 1 15,3 H19 A2,2 0 0 1 21,5 V9 A2,2 0 0 1 19,11 H15 A2,2 0 0 1 13,9 Z M3,15 A2,2 0 0 1 5,13 H9 A2,2 0 0 1 11,15 V19 A2,2 0 0 1 9,21 H5 A2,2 0 0 1 3,19 Z M13,15 A2,2 0 0 1 15,13 H19 A2,2 0 0 1 21,15 V19 A2,2 0 0 1 19,21 H15 A2,2 0 0 1 13,19 Z";
    public const string FolderFill = "M2,6.5 A2.5,2.5 0 0 1 4.5,4 H9 L11,6 H19.5 A2.5,2.5 0 0 1 22,8.5 V17.5 A2.5,2.5 0 0 1 19.5,20 H4.5 A2.5,2.5 0 0 1 2,17.5 Z";
    public const string ChevronRight = "M9,4.5 L16.5,12 L9,19.5";
    public const string AppArrow = "M7,3 H17 A4,4 0 0 1 21,7 V17 A4,4 0 0 1 17,21 H7 A4,4 0 0 1 3,17 V7 A4,4 0 0 1 7,3 Z M9.5,14.5 L15,9 M10.5,9 H15 V13.5";
    public const string ReplyFill = "M10,4.5 L2.5,11 L10,17.5 V13.6 C15.2,13.6 18.6,15.2 21.5,19.5 C20.8,13.4 17.4,8.9 10,8.4 Z";

    // Segoe Fluent glyphs
    public const string Shuffle = "", Repeat = "", RepeatOne = "", Checkmark = "", XMark = "", Terminal = "", Question = "";

    public static Path Icon(string data, double size, Brush fill, bool stroke = false, double strokeWidth = 2.2)
    {
        var p = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        if (stroke) { p.Stroke = fill; p.StrokeThickness = strokeWidth; p.StrokeStartLineCap = p.StrokeEndLineCap = PenLineCap.Round; p.StrokeLineJoin = PenLineJoin.Round; }
        else p.Fill = fill;
        return p;
    }

    public static TextBlock Glyph(string glyph, double size, Brush fill) =>
        new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = fill, VerticalAlignment = VerticalAlignment.Center };
}
