using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IsleWin.Claude;
using IsleWin.Core;
using IsleWin.Media;

namespace IsleWin;

/// The island, laid out and animated the way Isle (macOS) does it:
/// - collapsed: a black notch-shaped pill: art + equalizer │ 180 pt empty span │ marker + word;
/// - open: a 520 × 146 panel (taller, up to 230, when a face needs it) with an empty 32 pt band on top;
/// - springs for everything, width leading height on the way in, a taper as it collapses;
/// - hover zones instead of enter/leave: opens when the pointer reaches the pill (or 14 pt below it),
///   closes the moment it leaves a margin around the panel, ignores fast sideways sweeps,
///   and swells / leans toward a pointer that comes near.
public partial class IslandWindow : Window, IIslandHost
{
    const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    struct POINT { public int X, Y; }

    // NotchMetrics / CollapsedSize / NotchWindowController constants
    const double OpenW = 520, OpenH = 146, MaxH = 230, CollapsedH = 32, BandH = 32;
    const double Album = 22, Wave = 26, Dots = 16, Gap = 5, CutoutGap = 6, MinSide = 30, SidePad = 14 + 8; // padding + the 8 pt top flare the walls are inset by
    const double Cutout = 140; // the empty span between the two clusters (Isle uses the notch width, 180 fallback)
    const double OpenLip = 14, StayTop = 10, StaySide = 16, StayBottom = 26;
    const double SweepSpeed = 900, SweepAspect = 2.5;

    public ClaudeViewModel Claude { get; }
    readonly MediaService _media = new();
    readonly WaveformService _wave = new();
    readonly MusicView _music;
    readonly ClaudeView _claudeFace;
    readonly HomeView _home;
    readonly Crumb _crumb = new();
    readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly float[] _levels = new float[WaveformService.Bands];

    // collapsed content
    readonly Image _miniArt = new() { Stretch = Stretch.UniformToFill };
    readonly Border _miniArtBox;
    readonly Equalizer _miniEq = new() { Width = Wave, Height = 20, VerticalAlignment = VerticalAlignment.Center };
    readonly DotMatrix _miniDots = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _word = new() { FontFamily = IsleStyle.Text, FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _lead = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _trail = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    // motion
    readonly Spring _w = new(120), _h = new(CollapsedH), _top = new(8), _bottom = new(12);
    readonly Spring _swellW = new(0), _swellH = new(0), _lean = new(0);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastT, _collapsedW = 120, _openH = OpenH;
    Size? _collapseOrigin;

    // pointer
    Point _lastPtr; double _lastPtrT; bool _hasPtr;
    Point _dragFrom = new(-1000, -1000);

    IntPtr _hwnd, _prevFg;
    bool _open, _keyboard, _hold;
    IslandFace? _place; // null = Home
    UIElement? _shownFace;

    public IslandWindow()
    {
        InitializeComponent();
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = 0;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        Claude = new ClaudeViewModel(this);
        _music = new MusicView(_media);
        _claudeFace = new ClaudeView { DataContext = Claude, HorizontalAlignment = HorizontalAlignment.Center };
        _home = new HomeView(_media, Claude);
        _home.Picked += f => ShowPlace(f);
        _crumb.Home += () => ShowPlace(null);
        CrumbSlot.Content = _crumb;
        foreach (var f in new UIElement[] { _home, _music, _claudeFace }) { f.Visibility = Visibility.Collapsed; Faces.Children.Add(f); }

        _miniArtBox = new Border { Width = Album, Height = Album, Child = _miniArt, VerticalAlignment = VerticalAlignment.Center, Clip = new RectangleGeometry(new Rect(0, 0, Album, Album), 5, 5) };
        CollapsedRow.Children.Add(_lead); CollapsedRow.Children.Add(new Border { Width = Cutout }); CollapsedRow.Children.Add(_trail);
        _lead.Margin = new Thickness(0, 0, CutoutGap, 0); _trail.Margin = new Thickness(CutoutGap, 0, 0, 0);
        ExpandedRoot.Visibility = Visibility.Hidden;

        Deactivated += (_, _) => DropKeyboard();
        _tick.Tick += (_, _) =>
        {
            _wave.Snapshot(_levels); _miniEq.SetLevels(_levels);
            if (_open && _place == IslandFace.Music) { _music.SetLevels(_levels); _music.Tick(); }
        };
        _media.PropertyChanged += (_, _) => OnMedia();
        Claude.PropertyChanged += (_, _) => OnClaude();
        Loaded += (_, _) =>
        {
            _media.Start(); Claude.Start();
            OnMedia(); OnClaude();
            _w.Snap(_collapsedW);
            CompositionTarget.Rendering += OnFrame;
        };
        ShowPlace(null, animate: false);
    }

    // ---------------- content ----------------
    bool HasMusic => _media.HasTrack;
    bool HasClaude => Claude.HasSession;

    void OnMedia()
    {
        var p = _media.Palette;
        _miniArt.Source = _media.Artwork;
        _miniArtBox.Background = new LinearGradientBrush(p.Primary, p.Secondary, 45);
        _miniEq.SetPalette(p);
        Claude.SetPalette(p.Primary, p.Secondary, p.Accent);
        if (_media.IsPlaying && !_tick.IsEnabled) { _wave.Start(); _tick.Start(); }
        else if (!_media.IsPlaying && _tick.IsEnabled) { _tick.Stop(); _wave.Stop(); _miniEq.SetLevels(null); _music.SetLevels(null); }
        RebuildCollapsed();
    }

    bool _wasAttention;
    void OnClaude()
    {
        _miniDots.State = Claude.State;
        _miniDots.Primary = Claude.PalettePrimary; _miniDots.Secondary = Claude.PaletteSecondary; _miniDots.Accent = Claude.PaletteAccent;
        _word.Text = Claude.CollapsedWord;
        _word.Foreground = Claude.MarkerBrush;
        if (Claude.NeedsAttention && !_wasAttention) { ShowPlace(IslandFace.Claude); Open(); }
        _wasAttention = Claude.NeedsAttention;
        RebuildCollapsed();
        Retarget();
    }

    /// Isle's collapsed layouts: music + Claude split (art, waveform │ marker, word), Claude alone
    /// (marker │ word), music alone (art │ waveform).
    void RebuildCollapsed()
    {
        _lead.Children.Clear(); _trail.Children.Clear();
        void Add(Panel p, FrameworkElement e) { e.Margin = new Thickness(p.Children.Count > 0 ? Gap : 0, 0, 0, 0); p.Children.Add(e); }
        if (HasMusic && HasClaude)
        {
            Add(_lead, _miniArtBox); Add(_lead, _miniEq);
            _miniDots.Width = _miniDots.Height = Dots;
            Add(_trail, _miniDots); Add(_trail, _word);
        }
        else if (HasClaude)
        {
            _miniDots.Width = _miniDots.Height = Album;
            Add(_lead, _miniDots); Add(_trail, _word);
        }
        else if (HasMusic) { Add(_lead, _miniArtBox); Add(_trail, _miniEq); }
        _lead.MinWidth = _lead.Children.Count == 0 ? MinSide - CutoutGap : 0;
        _trail.MinWidth = _trail.Children.Count == 0 ? MinSide - CutoutGap : 0;
        CollapsedRow.Measure(new Size(double.PositiveInfinity, CollapsedH));
        _collapsedW = CollapsedRow.DesiredSize.Width + SidePad * 2;
        if (!_open) { _w.Use(IslandMotion.Open.R, IslandMotion.Open.D); _w.Target = _collapsedW; }
    }

    void ShowPlace(IslandFace? place, bool animate = true)
    {
        _place = place;
        UIElement next = place switch { IslandFace.Music => _music, IslandFace.Claude => _claudeFace, _ => _home };
        _crumb.Title = place switch { IslandFace.Music => "Music", IslandFace.Claude => "Claude", _ => "" };
        CrumbSlot.Visibility = place == null ? Visibility.Collapsed : Visibility.Visible;
        if (next == _shownFace) { Retarget(); return; }
        var prev = _shownFace; _shownFace = next;
        next.Visibility = Visibility.Visible;
        var d = TimeSpan.FromMilliseconds(animate ? 280 : 0);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut }; // Isle's cross-dissolve: easeInOut 0.28 s
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        if (prev != null)
        {
            var a = new DoubleAnimation(0, d) { EasingFunction = ease };
            a.Completed += (_, _) => { if (_shownFace != prev) prev.Visibility = Visibility.Collapsed; };
            prev.BeginAnimation(OpacityProperty, a);
        }
        if (place == IslandFace.Music) _music.Tick();
        Retarget();
    }

    /// The open panel's height for the shown face: 146, or taller (to 230) when its content asks.
    void Retarget()
    {
        if (_shownFace is not FrameworkElement f) return;
        f.Measure(new Size(OpenW - 44, double.PositiveInfinity));
        _openH = Math.Clamp(Math.Ceiling(f.DesiredSize.Height) + BandH + 2 + 8, OpenH, MaxH);
        if (_open) _h.Target = _openH;
    }

    // ---------------- open / close ----------------
    void Open()
    {
        if (_open) return;
        _open = true;
        if (Claude.NeedsAttention) ShowPlace(IslandFace.Claude, animate: false);
        Retarget();
        foreach (var s in new[] { _w, _h, _top, _bottom }) s.Use(IslandMotion.Open.R, IslandMotion.Open.D);
        _w.Target = OpenW; _h.Target = _openH; _top.Target = IslandMotion.OpenCorners.Top; _bottom.Target = IslandMotion.OpenCorners.Bottom;
        _swellW.Target = _swellH.Target = _lean.Target = 0;
        _collapseOrigin = null;
        ExpandedRoot.Visibility = Visibility.Visible;
        if (_place == IslandFace.Music) _music.Tick();
    }

    void Close_()
    {
        if (!_open) return;
        _open = false;
        _collapseOrigin = new Size(_w.Value, _h.Value); // the taper runs between these
        _w.Use(IslandMotion.CollapseWidth.R, IslandMotion.CollapseWidth.D);
        _h.Use(IslandMotion.CollapseHeight.R, IslandMotion.CollapseHeight.D);
        _top.Use(IslandMotion.CollapseCorners.R, IslandMotion.CollapseCorners.D);
        _bottom.Use(IslandMotion.CollapseCorners.R, IslandMotion.CollapseCorners.D);
        _w.Target = _collapsedW; _h.Target = CollapsedH; _top.Target = IslandMotion.CollapsedCorners.Top; _bottom.Target = IslandMotion.CollapsedCorners.Bottom;
    }

    // ---------------- per frame ----------------
    void OnFrame(object? s, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(t - _lastT, 0, 0.05);
        _lastT = t;
        Track(t);
        foreach (var sp in new[] { _w, _h, _top, _bottom, _swellW, _swellH, _lean }) sp.Step(dt);
        Draw();
    }

    Rect IslandRect()
    {
        double w = _w.Value + _swellW.Value, h = _h.Value + _swellH.Value;
        return new Rect((Width - w) / 2 + _lean.Value, 0, w, h);
    }

    void Track(double t)
    {
        if (PresentationSource.FromVisual(this) == null || !GetCursorPos(out var cp)) return;
        var p = PointFromScreen(new Point(cp.X, cp.Y));
        bool fast = false, sideways = false;
        if (_hasPtr && t - _lastPtrT > 0 && t - _lastPtrT < 0.2)
        {
            double dx = p.X - _lastPtr.X, dy = p.Y - _lastPtr.Y, dtt = t - _lastPtrT;
            fast = Math.Sqrt(dx * dx + dy * dy) / dtt > SweepSpeed;
            sideways = Math.Abs(dx) > Math.Abs(dy) * SweepAspect;
        }
        _lastPtr = p; _lastPtrT = t; _hasPtr = true;

        var r = IslandRect();
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!_open)
        {
            // open zone: the pill plus a 14 pt lip under it (and everything above it, to the screen edge)
            bool inOpen = p.X >= r.Left && p.X <= r.Right && p.Y <= r.Bottom + OpenLip;
            if (inOpen && !(fast && sideways)) { Open(); return; }
            Proximity(p, r);
        }
        else
        {
            var stay = new Rect(r.Left - StaySide, r.Top - StayTop, r.Width + StaySide * 2, r.Height + StayTop + StayBottom);
            bool dragging = down && stay.Contains(_dragFrom); // a drag that started on the panel (the scrubber) keeps it open
            if (!stay.Contains(p) && !_keyboard && !_hold && !dragging) Close_();
        }
        if (!down) _dragFrom = p;
    }

    /// Within 56 pt of the collapsed pill it swells up to 3 × 2 and leans up to 1.5 toward the pointer, in steps.
    void Proximity(Point p, Rect r)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right), dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        double closeness = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / IslandMotion.ProximityReach, 0, 1);
        double eased = closeness * closeness * (3 - 2 * closeness);
        int level = (int)Math.Round(eased * IslandMotion.ProximitySteps);
        double halfSpan = r.Width / 2 + IslandMotion.ProximityReach;
        double side = Math.Clamp((p.X - (r.Left + r.Width / 2)) / halfSpan, -1, 1);
        int lean = (int)Math.Round(side * eased * IslandMotion.LeanSteps);
        double f = (double)level / IslandMotion.ProximitySteps;
        foreach (var sp in new[] { _swellW, _swellH, _lean }) sp.Use(IslandMotion.Swell.R, IslandMotion.Swell.D);
        _swellW.Target = IslandMotion.MaxSwellW * f; _swellH.Target = IslandMotion.MaxSwellH * f;
        _lean.Target = Math.Clamp(IslandMotion.MaxLean * lean / IslandMotion.LeanSteps, -_swellW.Target, _swellW.Target);
    }

    void Draw()
    {
        var r = IslandRect();
        double pinch = !_open && _collapseOrigin is { } o ? IslandMotion.Taper(new Size(_w.Value, _h.Value), o, new Size(_collapsedW, CollapsedH)) : 0;
        var geo = IslandMotion.Outline(r.Width, r.Height, _top.Value, _bottom.Value, pinch);
        Canvas.SetLeft(Fill, r.Left); Fill.Data = geo;
        Canvas.SetLeft(Body, r.Left); Body.Width = r.Width; Body.Height = r.Height; Body.Clip = geo;

        // content: the open face fades in only past 65% of the way open; the pill's content is gone by 35%
        double progress = Math.Clamp((_w.Value - _collapsedW) / Math.Max(1, OpenW - _collapsedW), 0, 1.2);
        double openOpacity = Math.Clamp((progress - 0.65) / 0.3, 0, 1);
        double pillOpacity = Math.Clamp(1 - progress / 0.35, 0, 1);
        ExpandedRoot.Opacity = openOpacity;
        ExpandedRoot.Height = Math.Max(_openH, r.Height);
        Canvas.SetLeft(ExpandedRoot, (r.Width - OpenW) / 2);
        ExpandedRoot.IsHitTestVisible = _open && openOpacity > 0.5;
        if (!_open && openOpacity <= 0 && _w.Settled) ExpandedRoot.Visibility = Visibility.Hidden;

        CollapsedRow.Opacity = pillOpacity;
        CollapsedRow.Visibility = pillOpacity > 0 ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(CollapsedRow, (r.Width - CollapsedRow.DesiredSize.Width) / 2);
        Canvas.SetTop(CollapsedRow, (Math.Min(r.Height, CollapsedH + _swellH.Value) - CollapsedH) / 2);
    }

    // ---------------- IIslandHost (may be called from any thread) ----------------
    void UI(Action a) { if (Dispatcher.CheckAccess()) a(); else Dispatcher.BeginInvoke(a); }

    public void RequestKeyboard(bool want) => UI(() =>
    {
        if (_hwnd == IntPtr.Zero) return;
        if (want)
        {
            // no early-out: every click re-activates, so a stale flag can never lock the reply box
            if (!_keyboard) { var fg = GetForegroundWindow(); if (fg != _hwnd) _prevFg = fg; }
            _keyboard = true;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
            if (!IsActive) Activate();
        }
        else if (_keyboard)
        {
            bool ours = GetForegroundWindow() == _hwnd; // only hand focus back if we still own it
            DropKeyboard();
            if (ours && _prevFg != IntPtr.Zero && _prevFg != _hwnd) SetForegroundWindow(_prevFg);
        }
    });

    void DropKeyboard()
    {
        if (!_keyboard || _hwnd == IntPtr.Zero) return;
        _keyboard = false;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    public void RequestOpen(bool open) => UI(() => { _hold = open; if (open) Open(); });

    public void ShowFace(IslandFace face) => UI(() => ShowPlace(face));

    protected override void OnClosing(CancelEventArgs e) { CompositionTarget.Rendering -= OnFrame; _tick.Stop(); _wave.Stop(); base.OnClosing(e); }
}
