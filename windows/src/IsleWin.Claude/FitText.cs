using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IsleWin.Claude;

/// The approval card's text: mono 11 pt on 14 pt lines, a semibold bright `Label` then the muted `Text`, in at most
/// `Lines` lines. What doesn't fit is cut with an ellipsis in the middle (so a command's program and its end both
/// survive), or at the tail (`Tail`, a plan). WPF can't middle-truncate, so this measures and cuts itself.
public sealed class FitText : FrameworkElement
{
    const double LineHeight = 14, FontSize = 11;
    static readonly Typeface Face = new(IsleStyle.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Brush Muted = IsleStyle.White(0.7), Bright = IsleStyle.White(0.9);

    static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(FitText), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = Reg("Label", "");
    public static readonly DependencyProperty TextProperty = Reg("Text", "");
    public static readonly DependencyProperty LinesProperty = Reg("Lines", 2);
    public static readonly DependencyProperty TailProperty = Reg("Tail", false);
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public int Lines { get => (int)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public bool Tail { get => (bool)GetValue(TailProperty); set => SetValue(TailProperty, value); }

    FormattedText? _shown;

    FormattedText Make(string s, params (int Start, int Len)[] bold)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { LineHeight = LineHeight, MaxTextWidth = Math.Max(1, _width) };
        foreach (var (st, len) in bold)
            if (len > 0) { ft.SetFontWeight(FontWeights.SemiBold, st, len); ft.SetForegroundBrush(Bright, st, len); }
        return ft;
    }

    double _width = 448;
    bool Fits(FormattedText ft) => ft.Height <= Lines * LineHeight + 0.5;

    protected override Size MeasureOverride(Size available)
    {
        _width = double.IsInfinity(available.Width) ? 448 : available.Width;
        string label = Label ?? "", text = Text ?? "";
        string full = label.Length > 0 && text.Length > 0 ? label + "  " + text : label + text;
        int b = label.Length, n = full.Length;
        var whole = Make(full, (0, b));
        if (Fits(whole)) _shown = whole;
        else
        {
            int lo = 0, hi = n; // most characters kept that still fit
            FormattedText Cut(int k)
            {
                if (Tail) return Make(full[..k] + "…", (0, Math.Min(k, b)));
                int head = (k + 1) / 2, tail = k / 2, tailAt = n - tail;
                return Make(full[..head] + "…" + full[tailAt..], (0, Math.Min(head, b)), (head + 1, Math.Max(0, b - tailAt)));
            }
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Fits(Cut(mid))) lo = mid; else hi = mid - 1; }
            _shown = Cut(lo);
        }
        return new Size(_width, Math.Min(Lines, Math.Max(1, Math.Round(_shown.Height / LineHeight))) * LineHeight);
    }

    protected override void OnRender(DrawingContext dc) { if (_shown != null) dc.DrawText(_shown, new Point(0, 0)); }
}
