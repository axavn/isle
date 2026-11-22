using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using IsleWin.Claude;
using IsleWin.Core;

namespace IsleWin;

/// The island's two gestures:
/// - the pager: Home │ Music │ Claude side by side, slid between by a two-finger horizontal swipe
///   (or Shift + wheel, or a tilt wheel), the face following the fingers part-way;
/// - the carry: press and hold the open island still, and it comes off the top of the screen as a
///   floating pill you can drag anywhere; drop it near its spot (outlined while you carry) to dock it again.
public partial class IslandWindow
{
    // ---------------- pager ----------------
    const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    static readonly (double R, double D) PageSpring = (0.36, 0.86), FollowSpring = (0.10, 1.0);
    const double SwipeGain = 0.9, SwipeCommit = 0.35, FlickSpeed = 1.6, SwipeQuiet = 0.12;

    readonly Spring _page = new(0);
    bool _swiping; int _swipeBase; double _swipeOffset, _swipeVel, _lastSwipeT;
    DispatcherTimer? _swipeEnd;

    static int IndexOf(IslandFace? p) => p switch { IslandFace.Music => 1, IslandFace.Claude => 2, _ => 0 };
    static IslandFace? PlaceAt(int i) => i switch { 1 => IslandFace.Music, 2 => IslandFace.Claude, _ => null };
    FrameworkElement FaceAt(int i) => i switch { 1 => _music, 2 => _claudeFace, _ => _home };

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL && msg != WM_MOUSEWHEEL) return IntPtr.Zero;
        if (!_open || _carrying) return IntPtr.Zero;
        int delta = (short)((w.ToInt64() >> 16) & 0xFFFF);
        if (msg == WM_MOUSEWHEEL)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return IntPtr.Zero; // vertical scroll is left alone
            delta = -delta; // Shift + wheel down = the next face
        }
        handled = true;
        Swipe(delta);
        return IntPtr.Zero;
    }

    void Swipe(int delta)
    {
        int at = IndexOf(_place);
        // a wheel notch (a mouse, not a touchpad) is one face, at once
        if (!_swiping && Math.Abs(delta) >= 120 && delta % 120 == 0)
        {
            int to = Math.Clamp(at + Math.Sign(delta), 0, 2);
            if (to != at) ShowPlace(PlaceAt(to));
            return;
        }
        double t = _clock.Elapsed.TotalSeconds;
        if (!_swiping) { _swiping = true; _swipeBase = at; _swipeOffset = 0; _swipeVel = 0; _lastSwipeT = t; }
        double step = delta * SwipeGain / OpenW; // in faces
        double dt = Math.Max(1e-3, t - _lastSwipeT);
        _swipeVel = _swipeVel * 0.6 + step / dt * 0.4;
        _lastSwipeT = t;
        _swipeOffset = Math.Clamp(_swipeOffset + step, -1, 1); // never further than the neighbour

        double target = _swipeBase + _swipeOffset;
        if (target < 0) target = -Math.Min(0.12, -target * 0.25); // rubber band past the ends
        else if (target > 2) target = 2 + Math.Min(0.12, (target - 2) * 0.25);
        _page.Use(FollowSpring.R, FollowSpring.D);
        _page.Target = target;

        _swipeEnd ??= new DispatcherTimer(TimeSpan.FromSeconds(SwipeQuiet), DispatcherPriority.Normal, (_, _) => EndSwipe(), Dispatcher);
        _swipeEnd.Stop(); _swipeEnd.Start();
    }

    /// Windows says nothing when a touchpad swipe ends, so a short quiet gap ends it: past a third of the
    /// way, or flicked, it goes on to the neighbour; otherwise it goes back. Any coasting is part of the
    /// same gesture, which is capped at one face, so one swipe is one face.
    void EndSwipe()
    {
        _swipeEnd?.Stop();
        if (!_swiping) return;
        _swiping = false;
        int to = _swipeBase;
        if (_swipeOffset > SwipeCommit || (_swipeOffset > 0.05 && _swipeVel > FlickSpeed)) to++;
        else if (_swipeOffset < -SwipeCommit || (_swipeOffset < -0.05 && _swipeVel < -FlickSpeed)) to--;
        to = Math.Clamp(to, 0, 2);
        ShowPlace(PlaceAt(to));
    }

    void LayoutPages()
    {
        int current = IndexOf(_place);
        for (int i = 0; i < 3; i++)
        {
            var f = FaceAt(i);
            double d = i - _page.Value;
            ((TranslateTransform)f.RenderTransform).X = d * OpenW;
            var v = Math.Abs(d) < 0.999 ? Visibility.Visible : Visibility.Hidden;
            if (f.Visibility != v) f.Visibility = v;
            f.IsHitTestVisible = i == current && !_swiping;
        }
    }

    // ---------------- carry ----------------
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    const int WS_EX_TRANSPARENT = 0x20;

    const double HoldTime = 0.35, HoldSlop = 4, DockReach = 80;
    bool _floating, _carrying, _anchorBottom;
    Point? _pressAt; double _pressT;
    Window? _ghost; Path? _ghostPath;
    static string PlacementFile => System.IO.Path.Combine(IslePaths.Default.Root, "island.json");

    Point DockCenter => new(SystemParameters.PrimaryScreenWidth / 2, CollapsedH / 2);

    Point CursorDip()
    {
        GetCursorPos(out var cp);
        var p = PointFromScreen(new Point(cp.X, cp.Y));
        return new Point(Left + p.X, Top + p.Y);
    }

    /// A press counts toward a carry only on the island's own surface, not on anything it would click.
    void OnPress(MouseButtonEventArgs e)
    {
        if (!_open || _carrying) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != this; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or TextBoxBase or Thumb or ScrollBar) return;
            if (d is FrameworkElement { Cursor: var c } && c == Cursors.Hand) return;
        }
        _pressAt = CursorDip(); _pressT = _clock.Elapsed.TotalSeconds;
    }

    /// Runs before hover tracking each frame; true while the island is being carried.
    bool CarryFrame(double t)
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (_pressAt is { } at && !_carrying)
        {
            var c = CursorDip();
            if (!down || (c - at).Length > HoldSlop) _pressAt = null;
            else if (t - _pressT >= HoldTime) BeginCarry(c);
        }
        if (!_carrying) return false;
        var p = CursorDip();
        if (!down) { EndCarry(p); return false; }
        MoveTo(p, bottom: false);
        UpdateGhost(p);
        return true;
    }

    void BeginCarry(Point c)
    {
        _pressAt = null; _carrying = true;
        if (_keyboard) RequestKeyboard(false);
        _hold = false;
        Close_();
        SetFloating(true, bottom: false);
        MoveTo(c, bottom: false);
        ShowGhost();
    }

    void EndCarry(Point p)
    {
        _carrying = false;
        _ghost?.Hide();
        if ((p - DockCenter).Length < DockReach) Dock();
        else { SetFloating(true, LowerHalf(p)); MoveTo(p, _anchorBottom); }
        SavePlacement(p);
    }

    void Dock()
    {
        SetFloating(false, false);
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = 0;
    }

    void SetFloating(bool floating, bool bottom)
    {
        _floating = floating; _anchorBottom = floating && bottom;
        _cutout.Width = floating ? 0 : Cutout; // a floating pill has no camera to straddle
        RebuildCollapsed();
        var (top, bot) = _open ? OpenCornersNow : CollapsedCornersNow;
        _top.Target = top; _bottom.Target = bot;
    }

    (double Top, double Bottom) OpenCornersNow => _floating ? (22, 22) : IslandMotion.OpenCorners;
    (double Top, double Bottom) CollapsedCornersNow => _floating ? (CollapsedH / 2, CollapsedH / 2) : IslandMotion.CollapsedCorners;

    /// Puts the collapsed pill's centre at `c`; anchored at the bottom it opens upward.
    void MoveTo(Point c, bool bottom)
    {
        Left = c.X - Width / 2;
        Top = bottom ? c.Y + CollapsedH / 2 - Height : c.Y - CollapsedH / 2;
    }

    bool LowerHalf(Point dip)
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var dev = m.Transform(dip);
        var mon = MonitorFromPoint(new POINT { X = (int)dev.X, Y = (int)dev.Y }, 2 /* nearest */);
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return dip.Y > SystemParameters.PrimaryScreenHeight / 2;
        return dev.Y > (mi.Work.T + mi.Work.B) / 2.0;
    }

    void ShowGhost()
    {
        if (_ghost == null)
        {
            _ghostPath = new Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), Stroke = IsleStyle.White(0.45),
                StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 3, 3 },
            };
            _ghost = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false,
                Width = 400, Height = CollapsedH + 4, Content = new Canvas { Children = { _ghostPath } },
            };
            _ghost.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_ghost).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            };
        }
        // the docked pill's outline, where it would land
        double w = DockedWidth();
        _ghost.Width = w; _ghost.Left = (SystemParameters.PrimaryScreenWidth - w) / 2; _ghost.Top = 0;
        _ghostPath!.Data = IslandMotion.Outline(w, CollapsedH, IslandMotion.CollapsedCorners.Top, IslandMotion.CollapsedCorners.Bottom, 0);
        _ghost.Opacity = 0;
        _ghost.Show();
    }

    double DockedWidth() => _collapsedW + (_floating ? Cutout : 0);

    void UpdateGhost(Point p)
    {
        if (_ghost == null) return;
        double dist = (p - DockCenter).Length;
        _ghost.Opacity = Math.Clamp(1 - (dist - DockReach) / 260, 0.2, 1);
        if (_ghostPath != null) _ghostPath.Fill = new SolidColorBrush(Color.FromArgb((byte)(dist < DockReach ? 0xE6 : 0x8C), 0, 0, 0));
    }

    void SavePlacement(Point p)
    {
        try
        {
            Directory.CreateDirectory(IslePaths.Default.Root);
            AtomicFile.WriteAllText(PlacementFile, new JsonObject { ["floating"] = _floating, ["x"] = p.X, ["y"] = p.Y }.ToJsonString());
        }
        catch { }
    }

    void LoadPlacement()
    {
        try
        {
            if (!File.Exists(PlacementFile) || JsonNode.Parse(File.ReadAllText(PlacementFile)) is not JsonObject o) return;
            if (o["floating"]?.GetValue<bool>() != true) return;
            var p = new Point(o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>());
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!vs.Contains(p)) return; // that screen is gone: stay docked
            SetFloating(true, LowerHalf(p));
            MoveTo(p, _anchorBottom);
            _top.Snap(CollapsedCornersNow.Top); _bottom.Snap(CollapsedCornersNow.Bottom);
        }
        catch { }
    }
}
