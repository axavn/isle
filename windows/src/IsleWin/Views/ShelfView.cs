using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using IsleWin.Claude;
using IsleWin.Core;
using IsleWin.Shelf;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace IsleWin;

/// Isle's ShelfLayout (ShelfExpandedView.swift): one row of 5 large cells for up to 5 items, two rows of
/// smaller cells for 6 to 10. The height depends only on the row count.
public static class ShelfLayout
{
    public const int Columns = 5;
    public const double ColumnWidth = 340, CellWidth = 64, CellGap = 5, StackSide = 114, StackRaise = 16, FanSide = 60;
    public const double CutoutClearance = 4, HeadlineHeight = 14, HeadlineGap = 3, BarHeight = 24, BarBottom = 2, BarGap = 6, RowGap = 4;

    public readonly record struct Cell(double Side, double Padding, double Spacing, double NameSize, double NameHeight)
    {
        public double Height => Padding + Side + Spacing + NameHeight + Padding;
    }
    public static readonly Cell Large = new(56, 3, 3, IsleStyle.Caption, 26), Small = new(44, 2, 2, IsleStyle.Micro, 22);

    public static int RowsFor(int count) => count > Columns ? 2 : 1;
    public static Cell CellFor(int rows) => rows > 1 ? Small : Large;
    /// 144 for one row (the agenda's height), 184 for two.
    public static double ContentHeight(int rows)
    {
        var heading = rows > 1 ? 0 : HeadlineHeight + HeadlineGap;
        return CutoutClearance + heading + CellFor(rows).Height * rows + RowGap * (rows - 1) + BarGap + BarHeight + BarBottom;
    }
}

/// An item's picture: its icon at once, then the shell's thumbnail cross-fading in over it (0.25 s).
/// `fills` crops the thumbnail to the square (the Home tile's lead); otherwise it is fitted, top-aligned.
public sealed class ShelfPreviewImage : Grid
{
    readonly Image _icon = new() { Stretch = Stretch.Uniform };
    readonly Image _thumb = new();
    readonly Border _thumbBox = new() { Opacity = 0, BorderBrush = IsleStyle.White(0.16), BorderThickness = new Thickness(0.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    string? _path; bool _isFolder, _started; int _token;

    public ShelfPreviewImage(double side, double corner, bool fills = false)
    {
        Width = Height = side;
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);
        _thumb.Stretch = fills ? Stretch.UniformToFill : Stretch.Uniform;
        _thumbBox.CornerRadius = new CornerRadius(corner); _thumbBox.Child = _thumb;
        if (fills) { _thumbBox.Width = _thumbBox.Height = side; _thumbBox.Clip = new RectangleGeometry(new Rect(0, 0, side, side), corner, corner); }
        else _thumbBox.MaxWidth = _thumbBox.MaxHeight = side;
        Children.Add(_icon); Children.Add(_thumbBox);
        Loaded += (_, _) => { if (!_started && _path != null) Start(); };
    }

    public void SetItem(string path, bool isFolder)
    {
        if (_path == path && _isFolder == isFolder) return;
        _path = path; _isFolder = isFolder; _started = false; _token++;
        _icon.Source = null; _icon.Opacity = 1; _thumb.Source = null; _thumbBox.Opacity = 0;
        if (IsLoaded) Start();
    }

    void Start()
    {
        _started = true; var token = _token;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ShellThumbnails.Load(_path!, _isFolder, 64, dpi, Dispatcher, (src, isThumb) =>
        {
            if (token != _token) return;
            if (!isThumb) { _icon.Source = src; return; }
            _thumb.Source = src;
            _thumbBox.Opacity = 0;
            _thumbBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.25)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            _icon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.25)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
        }, () => token != _token);
    }
}

/// Fits a name into `lines` lines with the middle elided, so an extension or a "copy 2" stays visible.
static class MiddleText
{
    public static string Fit(string s, double width, FontFamily family, double size, FontWeight weight, int lines, double dpi)
    {
        double Height(string t) => new FormattedText(t, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, dpi) { MaxTextWidth = width, TextAlignment = TextAlignment.Center }.Height;
        var line = Height("Ag");
        bool Fits(string t) => Height(t) <= line * lines + 0.5;
        if (Fits(s)) return s;
        int lo = 1, hi = s.Length - 1, best = 1; // keep `k` characters around an ellipsis
        string Make(int k) { var head = (k + 1) / 2; var tail = k / 2; return s[..head] + "…" + s[^tail..]; }
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Fits(Make(mid))) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return Make(best);
    }
}

/// Isle's Shelf face: the last three items fanned in a square, the count under it, a grid of the items
/// (drag one out, click to select), and the buttons (Start Claude here, Clear / Remove).
/// The window sets the face's height from `ContentHeight`, listens to `ContentHeightChanged`, and calls
/// `SetPanelHovered` as the panel opens and closes.
public sealed class ShelfView : Grid
{
    sealed class Cell
    {
        public required string Path; public required bool Small;
        public required Border Root; public required ShelfPreviewImage Image; public required TextBlock Name;
    }

    /// True while a drag that began on the shelf is in flight. The window ignores drags it starts itself.
    public static bool DraggingOut { get; private set; }
    public event Action? DragOutStarted, DragOutEnded, ContentHeightChanged;

    readonly ShelfStore _store;
    readonly Dictionary<string, Cell> _cells = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    readonly Canvas _stack = new() { Width = ShelfLayout.StackSide, Height = ShelfLayout.StackSide };
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, ShelfLayout.BarBottom + 1) };
    readonly TextBlock _headline = new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Meta, FontWeight = FontWeights.Medium, Foreground = IsleStyle.White(IsleStyle.MetaTone), Height = ShelfLayout.HeadlineHeight, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Canvas _grid = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Width = ShelfLayout.ColumnWidth };
    readonly Grid _right = new() { Width = ShelfLayout.ColumnWidth, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _claudeBtn, _clearBtn;
    readonly TextBlock _clearTitle, _clearGlyph;
    bool _hovered; int _rows = 1;
    Point _down; Cell? _downCell; bool _dragged;

    public ShelfView(ShelfStore store)
    {
        _store = store;
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
        Width = ShelfLayout.StackSide + 14 + ShelfLayout.ColumnWidth;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShelfLayout.StackSide) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShelfLayout.ColumnWidth) });

        // left: the fanned stack (lifted into the band) and the count
        var left = new Grid();
        var plate = new Border { Width = ShelfLayout.StackSide, Height = ShelfLayout.StackSide, CornerRadius = new CornerRadius(14), Background = IsleStyle.White(0.07), VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0, -ShelfLayout.StackRaise), Child = _stack };
        left.Children.Add(plate); left.Children.Add(_count);
        Children.Add(left);

        // right: headline, grid, buttons
        _claudeBtn = Button(IsleStyle.Terminal, out _, "Start Claude here", false);
        _clearBtn = Button(IsleStyle.XMark, out _clearTitle, "Clear", true);
        _clearGlyph = (TextBlock)((StackPanel)_clearBtn.Child).Children[0];
        _claudeBtn.MouseLeftButtonUp += (_, _) => StartClaudeHere();
        _clearBtn.MouseLeftButtonUp += (_, _) => ClearOrRemove();
        var bar = new DockPanel { Height = ShelfLayout.BarHeight, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, ShelfLayout.BarBottom), LastChildFill = false };
        DockPanel.SetDock(_clearBtn, Dock.Right);
        bar.Children.Add(_clearBtn); bar.Children.Add(_claudeBtn);
        _claudeBtn.Margin = new Thickness(0, 0, 8, 0);
        _right.Children.Add(_headline); _right.Children.Add(_grid); _right.Children.Add(bar);
        SetColumn(_right, 2); Children.Add(_right);

        _store.Changed += OnStoreChanged;
        Unloaded += (_, _) => _store.Changed -= OnStoreChanged;
        Loaded += (_, _) => { _store.Changed -= OnStoreChanged; _store.Changed += OnStoreChanged; Refresh(); };
        Refresh();
    }

    void OnStoreChanged() { if (Dispatcher.CheckAccess()) Refresh(); else Dispatcher.BeginInvoke(Refresh); }

    public double ContentHeight => ShelfLayout.ContentHeight(_rows);
    public int Rows => _rows;

    /// Isle's rule: while the panel is open the row count only grows, so the panel's edge never moves out from
    /// under the pointer when an item is removed. Call with true when the panel opens (also call
    /// `ShelfStore.Prune()` then), false when it closes (the layout re-fits).
    public void SetPanelHovered(bool open)
    {
        if (_hovered == open) return;
        _hovered = open;
        if (!open) Refresh();
    }

    void Refresh()
    {
        var items = _store.Items;
        var live = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        _selected.RemoveWhere(p => !live.Contains(p));

        var needed = ShelfLayout.RowsFor(items.Count);
        var rows = _hovered ? Math.Max(_rows, needed) : needed;
        var rowsChanged = rows != _rows; _rows = rows;
        var cellSpec = ShelfLayout.CellFor(_rows);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var gone in _cells.Keys.Where(k => !live.Contains(k)).ToList()) _cells.Remove(gone);
        _grid.Children.Clear();
        for (var n = 0; n < items.Count; n++)
        {
            var it = items[n];
            if (!_cells.TryGetValue(it.Path, out var c) || c.Small != (_rows > 1)) _cells[it.Path] = c = MakeCell(it, cellSpec, dpi);
            Canvas.SetLeft(c.Root, n % ShelfLayout.Columns * (ShelfLayout.CellWidth + ShelfLayout.CellGap));
            Canvas.SetTop(c.Root, n / ShelfLayout.Columns * (cellSpec.Height + ShelfLayout.RowGap));
            _grid.Children.Add(c.Root);
            StyleCell(c);
        }

        // geometry of the right column: 4 clearance, a headline on one row, then the grid
        var heading = _rows > 1 ? 0 : ShelfLayout.HeadlineHeight + ShelfLayout.HeadlineGap;
        Height = ContentHeight; _right.Height = ContentHeight;
        _headline.Margin = new Thickness(0, ShelfLayout.CutoutClearance, 0, 0);
        _headline.Visibility = _rows > 1 ? Visibility.Collapsed : Visibility.Visible;
        _grid.Margin = new Thickness(0, ShelfLayout.CutoutClearance + heading, 0, 0);
        _grid.Height = cellSpec.Height * _rows + ShelfLayout.RowGap * (_rows - 1);
        _headline.Text = _selected.Count == 0 ? "Drag one out to use it · click to select"
            : $"{_selected.Count} selected · the buttons act on {(_selected.Count > 1 ? "them" : "it")}";

        // the stack: the last three, latest on top
        _stack.Children.Clear();
        var last = items.Skip(Math.Max(0, items.Count - 3)).ToList();
        for (var i = 0; i < last.Count; i++)
        {
            var step = i - (last.Count - 1) / 2.0;
            var img = new ShelfPreviewImage(ShelfLayout.FanSide, 5);
            img.SetItem(last[i].Path, last[i].IsFolder);
            img.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 3, ShadowDepth = 1, Direction = 270 };
            var half = ShelfLayout.FanSide / 2;
            img.RenderTransform = new TransformGroup { Children = { new RotateTransform(step * 9, half, half), new TranslateTransform(step * 17, Math.Abs(step) * 3) } };
            Canvas.SetLeft(img, (ShelfLayout.StackSide - ShelfLayout.FanSide) / 2); Canvas.SetTop(img, (ShelfLayout.StackSide - ShelfLayout.FanSide) / 2);
            _stack.Children.Add(img);
        }

        _count.Inlines.Clear();
        _count.Inlines.Add(new System.Windows.Documents.Run(items.Count.ToString()) { FontFamily = IsleStyle.Display, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } });
        _count.Inlines.Add(new System.Windows.Documents.Run(_store.IsFull ? " · full" : items.Count == 1 ? " item" : " items") { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Body, FontWeight = FontWeights.SemiBold, Foreground = IsleStyle.White(IsleStyle.Secondary) });

        var folder = ClaudeFolder(items);
        _claudeBtn.Visibility = folder != null ? Visibility.Visible : Visibility.Collapsed;
        _claudeBtn.ToolTip = folder;
        _clearTitle.Text = _selected.Count == 0 ? "Clear" : "Remove";
        _clearGlyph.Text = _selected.Count == 0 ? IsleStyle.XMark : "";
        _clearBtn.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (rowsChanged) ContentHeightChanged?.Invoke();
    }

    // the folder "Start Claude here" would open: with nothing selected the shelf's only folder, with a
    // selection the one folder among the selected
    string? ClaudeFolder(IReadOnlyList<ShelfItem> items)
    {
        var pool = _selected.Count == 0 ? items : items.Where(i => _selected.Contains(i.Path)).ToList();
        var folders = pool.Where(i => i.IsFolder).ToList();
        return folders.Count == 1 ? folders[0].Path : null;
    }

    void StartClaudeHere()
    {
        if (ClaudeFolder(_store.Items) is not { } folder) return;
        _store.Prune(); // Isle prunes first; the folder is re-checked on the worker anyway
        StartClaude.Open(folder);
    }

    void ClearOrRemove()
    {
        if (_selected.Count == 0) _store.Clear();
        else { foreach (var p in _selected.ToList()) _store.Remove(p); _selected.Clear(); Refresh(); }
    }

    Cell MakeCell(ShelfItem it, ShelfLayout.Cell spec, double dpi)
    {
        var img = new ShelfPreviewImage(spec.Side, 4);
        img.SetItem(it.Path, it.IsFolder);
        var name = new TextBlock
        {
            FontFamily = IsleStyle.Text, FontSize = spec.NameSize, FontWeight = FontWeights.Medium, Foreground = Brushes.White, Opacity = 0.9,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = ShelfLayout.CellWidth - 2 * spec.Padding, Height = spec.NameHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Text = MiddleText.Fit(it.Name, ShelfLayout.CellWidth - 2 * spec.Padding, IsleStyle.Text, spec.NameSize, FontWeights.Medium, 2, dpi),
        };
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(img); col.Children.Add(new Border { Height = spec.Spacing }); col.Children.Add(name);
        var root = new Border { Width = ShelfLayout.CellWidth, Padding = new Thickness(spec.Padding - 1), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Child = col, ToolTip = it.Path };
        var cell = new Cell { Path = it.Path, Small = _rows > 1, Root = root, Image = img, Name = name };
        root.PreviewMouseLeftButtonDown += (_, e) => { _down = e.GetPosition(root); _downCell = cell; _dragged = false; };
        root.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _downCell != cell || _dragged) return;
            var d = e.GetPosition(root) - _down;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragged = true; DragOut(root, cell.Path);
        };
        root.MouseLeftButtonUp += (_, _) => { if (!_dragged && _downCell == cell) Toggle(cell.Path); _downCell = null; };
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show in Explorer" };
        show.Click += (_, _) => Reveal(cell.Path, it.IsFolder);
        var remove = new MenuItem { Header = "Remove from Shelf" };
        remove.Click += (_, _) => _store.Remove(cell.Path);
        menu.Items.Add(show); menu.Items.Add(remove);
        root.ContextMenu = menu;
        HoverGrow.Attach(root);
        return cell;
    }

    void StyleCell(Cell c)
    {
        var on = _selected.Contains(c.Path);
        c.Root.Background = on ? IsleStyle.White(0.16) : Brushes.Transparent;
        c.Root.BorderBrush = on ? IsleStyle.White(0.5) : Brushes.Transparent;
    }

    void Toggle(string path)
    {
        if (!_selected.Remove(path)) _selected.Add(path);
        Refresh();
    }

    // Copy or Link only, never Move: the shelf holds the original, and the item stays on it after the drag.
    // If the destination did move the file, the prune afterwards drops it.
    void DragOut(DependencyObject source, string path)
    {
        DraggingOut = true; DragOutStarted?.Invoke();
        try { DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link); }
        catch { }
        finally { DraggingOut = false; DragOutEnded?.Invoke(); _store.Prune(); }
    }

    static void Reveal(string path, bool folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", folder ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // a capsule button: glyph 10 bold, title 12 semibold; filled 12% white, or outlined for Clear / Remove
    static Border Button(string glyph, out TextBlock title, string text, bool outlined)
    {
        title = new TextBlock { Text = text, FontFamily = IsleStyle.Text, FontSize = IsleStyle.Body, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var g = IsleStyle.Glyph(glyph, 10, Brushes.White); g.FontWeight = FontWeights.Bold; g.Margin = new Thickness(0, 0, 5, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(g); row.Children.Add(title);
        var b = new Border { Height = ShelfLayout.BarHeight, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(12), Child = row, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        if (outlined) { b.BorderBrush = IsleStyle.White(0.45); b.BorderThickness = new Thickness(1); b.Background = Brushes.Transparent; }
        else b.Background = IsleStyle.White(0.12);
        HoverGrow.Attach(b);
        return b;
    }
}
