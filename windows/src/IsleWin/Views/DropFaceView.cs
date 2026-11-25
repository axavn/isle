using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IsleWin.Claude;
using IsleWin.Core;
using IsleWin.Shelf;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace IsleWin;

public enum DropTarget { StartClaude, KeepOnShelf }

/// Isle's drop face (DropFaceView.swift): a headline naming what is being dragged and a row of dashed
/// targets, the one under the pointer lit. Shown on the standard 520 x 146 panel in place of the open face.
/// The window feeds it the dragged paths (`SetItems`), asks `TargetAt` on every DragOver, calls `Highlight`
/// with the answer, and on Drop runs `Perform` on the target (nothing happens when the drop isn't on one).
public sealed class DropFaceView : Grid
{
    sealed class Tile
    {
        public required DropTarget Target; public required Border Root; public required Rectangle Outline; public required SolidColorBrush Fill, Stroke; public required TextBlock Detail;
    }

    readonly ShelfStore _store;
    readonly TextBlock _headline = new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Meta, FontWeight = FontWeights.Medium, Foreground = IsleStyle.White(IsleStyle.MetaTone), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 0) };
    readonly Grid _row = new();
    readonly List<Tile> _tiles = new();
    string[] _paths = Array.Empty<string>();
    bool _oneFolder; int _wouldKeep;
    DropTarget? _lit;

    public DropFaceView(ShelfStore store)
    {
        _store = store;
        Margin = new Thickness(0, 0, 0, 2);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        RowDefinitions.Add(new RowDefinition());
        Children.Add(_headline);
        SetRow(_row, 2); Children.Add(_row);
    }

    public string[] Paths => _paths;

    /// The dragged paths: rebuilds the headline and which targets this drop offers.
    public void SetItems(string[] paths)
    {
        _paths = ShelfStore.Unique(paths);
        var folders = _paths.Select(p => Directory.Exists(p)).ToArray();
        var nFolders = folders.Count(f => f);
        _oneFolder = _paths.Length == 1 && folders[0];
        _wouldKeep = _store.WouldKeep(_paths);
        _lit = null;

        var names = string.Join(", ", _paths.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p));
        _headline.Text = $"{ShelfStore.KindsLine(nFolders, _paths.Length - nFolders)} · {names}";

        _row.Children.Clear(); _row.ColumnDefinitions.Clear(); _tiles.Clear();
        var targets = new List<DropTarget>();
        if (_oneFolder) targets.Add(DropTarget.StartClaude);
        targets.Add(DropTarget.KeepOnShelf);
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) _row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            _row.ColumnDefinitions.Add(new ColumnDefinition());
            var t = MakeTile(targets[i]);
            Grid.SetColumn(t.Root, i * 2);
            _row.Children.Add(t.Root); _tiles.Add(t);
        }
    }

    /// Whether dropping on this target would do something (Keep with nothing to keep does not).
    public bool CanPerform(DropTarget t) => t switch
    {
        DropTarget.StartClaude => _oneFolder,
        _ => _wouldKeep > 0,
    };

    /// The target under `p` (DIPs, relative to this view) that would do something, else null.
    public DropTarget? TargetAt(Point p)
    {
        foreach (var t in _tiles)
        {
            if (!CanPerform(t.Target) || !t.Root.IsVisible) continue;
            var r = t.Root.TransformToAncestor(this).TransformBounds(new Rect(t.Root.RenderSize));
            if (r.Contains(p)) return t.Target;
        }
        return null;
    }

    /// Lights one target (or none) over 0.12 s.
    public void Highlight(DropTarget? target)
    {
        if (_lit == target) return;
        _lit = target;
        foreach (var t in _tiles)
        {
            var on = t.Target == target;
            Animate(t.Fill, on ? 0.16 : 0.05); Animate(t.Stroke, on ? 1 : 0.3);
            t.Outline.StrokeThickness = on ? 2 : 1.2;
            t.Outline.StrokeDashArray = on ? null : new DoubleCollection { 4 / 1.2, 3 / 1.2 };
        }
    }

    /// Runs the action for a dropped-on target. Returns true when it put something on the shelf, so the window
    /// can show the Shelf face (Keep). Start Claude leaves the panel as it was. Call outside the OLE drop
    /// callback (Dispatcher.BeginInvoke).
    public bool Perform(DropTarget target)
    {
        if (!CanPerform(target)) return false;
        switch (target)
        {
            case DropTarget.StartClaude: StartClaude.Open(_paths[0]); return false;
            default: return _store.Add(_paths) > 0;
        }
    }

    static void Animate(SolidColorBrush b, double alpha) =>
        b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255), TimeSpan.FromSeconds(0.12)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });

    Tile MakeTile(DropTarget target)
    {
        var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(0.05 * 255), 255, 255, 255));
        var stroke = new SolidColorBrush(Color.FromArgb((byte)Math.Round(0.3 * 255), 255, 255, 255));
        var outline = new Rectangle { RadiusX = 14, RadiusY = 14, Fill = fill, Stroke = stroke, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4 / 1.2, 3 / 1.2 } };

        var (glyph, title, detail) = target == DropTarget.StartClaude
            ? (IsleStyle.Terminal, "Start Claude here", Tilde(_paths[0]))
            : ("", "Keep on shelf", KeepDetail());
        var icon = new TextBlock { Text = glyph, FontFamily = IsleStyle.Icons, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var iconBox = new Border { Height = 24, Child = icon };
        var head = new TextBlock { Text = title, FontFamily = IsleStyle.Text, FontSize = IsleStyle.Headline, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var det = new TextBlock { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Caption, FontWeight = FontWeights.Medium, Foreground = IsleStyle.White(IsleStyle.Secondary), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = target == DropTarget.StartClaude ? _paths[0] : null };
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        col.Children.Add(iconBox); col.Children.Add(new Border { Height = 4 }); col.Children.Add(head); col.Children.Add(new Border { Height = 4 }); col.Children.Add(det);

        var g = new Grid(); g.Children.Add(outline); g.Children.Add(col);
        var root = new Border { Child = g, Opacity = CanPerform(target) ? 1 : 0.4 };
        var tile = new Tile { Target = target, Root = root, Outline = outline, Fill = fill, Stroke = stroke, Detail = det };
        // the path is middle-elided to fit once it has a width
        det.Text = detail;
        if (target == DropTarget.StartClaude)
            root.SizeChanged += (_, e) => det.Text = MiddleFit(detail, e.NewSize.Width - 20, det);
        return tile;
    }

    string KeepDetail()
    {
        var n = _paths.Length;
        if (_wouldKeep == n && n > 0) return "until you clear it";
        if (_wouldKeep > 0) return $"keeps {_wouldKeep} of {n} · holds {ShelfStore.Capacity}";
        return _store.Room == 0 ? $"shelf is full · holds {ShelfStore.Capacity}" : "already on the shelf";
    }

    static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (path.Length == home.Length || path[home.Length] is '\\' or '/')
            ? "~" + path[home.Length..] : path;
    }

    static string MiddleFit(string s, double width, TextBlock like)
    {
        if (width <= 0) return s;
        var dpi = VisualTreeHelper.GetDpi(like).PixelsPerDip;
        double W(string t) => new FormattedText(t, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(like.FontFamily, FontStyles.Normal, like.FontWeight, FontStretches.Normal), like.FontSize, Brushes.White, dpi).Width;
        if (W(s) <= width) return s;
        int lo = 1, hi = s.Length - 1, best = 1;
        string Make(int k) => s[..((k + 1) / 2)] + "…" + s[^(k / 2)..];
        while (lo <= hi) { var mid = (lo + hi) / 2; if (W(Make(mid)) <= width) { best = mid; lo = mid + 1; } else hi = mid - 1; }
        return Make(best);
    }
}
