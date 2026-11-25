using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IsleWin.Claude;

namespace IsleWin;

/// Taskbar mode: the island is a pill on the taskbar the size of Windows 11's search box, beside the
/// Widgets (weather) button. It doesn't morph open: like Search, a panel pops up above the taskbar,
/// sliding up as it fades in. The pill follows the taskbar (position, auto-hide, a top taskbar), hides
/// during full-screen apps, and keeps itself above the taskbar, which comes to the front when clicked.
public partial class IslandWindow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    const double TbGap = 8;            // pill ↔ weather button
    const double PanelGap = 12;        // taskbar ↔ panel, as Windows' flyouts sit
    const double PanelRise = 24;       // how far the panel slides up as it appears
    const double PanelRadius = 12;
    const double PillBelow = 8;        // window bottom ↔ pill bottom (room for the swell)
    static readonly Size SearchFallback = new(220, 32);
    static readonly (double R, double D) PopIn = (0.30, 0.84), PopOut = (0.16, 1.0);

    bool _taskbar, _tbHidden, _tbBottom = true;
    double _pillX, _pillY, _panelX, _panelHClose;
    Size _pill = SearchFallback;
    Rect _panelRect;
    readonly Spring _pop = new(0);
    /// Device pixels, from UI Automation: the weather, the search box, and the icons (Start, Search, Task View
    /// and the app buttons) as one span.
    readonly record struct TaskbarParts(Rect? Widgets, Rect? Search, Rect? Apps);
    TaskbarParts _parts;
    DispatcherTimer? _tbTimer;
    volatile bool _tbWatch; Task? _tbWatchTask;
    double _tbRowMin = 120; // the narrowest pill that still holds its content
    Window? _tbGhost; Path? _tbGhostPath;

    // ---------------- placement ----------------
    public void PlaceOnTaskbar()
    {
        if (_carrying) return;
        _taskbar = true;
        SetFloating(true, false);
        _anchorBottom = false;
        _top.Snap(CollapsedCornersNow.Top); _bottom.Snap(CollapsedCornersNow.Bottom);
        if (_open) { _open = false; FaceKeys(false); _pop.Snap(0); }
        _tbTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => TaskbarTick(), Dispatcher);
        _tbTimer.Start();
        WatchTaskbar(true);
        TaskbarTick();
        SavePlacement(default);
    }

    public void PlaceAtTop()
    {
        if (_carrying) return;
        LeaveTaskbar();
        Dock();
        SavePlacement(default);
    }

    void LeaveTaskbar()
    {
        if (!_taskbar) return;
        _taskbar = false; _tbHidden = false;
        _tbTimer?.Stop();
        if (!_carrying) WatchTaskbar(false);
        _pop.Snap(0);
        PanelFill.Visibility = Visibility.Hidden;
        Opacity = 1; IsHitTestVisible = true;
    }

    Matrix ToDip => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
    Rect Dip(Rect dev) { var m = ToDip; return new Rect(m.Transform(dev.TopLeft), m.Transform(dev.BottomRight)); }

    /// Where the pill goes on the taskbar (screen DIPs), the work area it pops into, whether the taskbar is
    /// along the bottom, and whether there's room for the pill at all. Null when there's no usable taskbar
    /// (none, or a vertical one).
    (Rect Pill, Rect Work, bool Bottom, RECT Mon, RECT Tray, bool Fits)? TaskbarSlot()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tr)) return null;
        if (tr.B - tr.T > tr.R - tr.L) return null; // left/right taskbar: no room for a pill along it
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(tray, 2), ref mi)) return null;
        var tb = Dip(new Rect(tr.L, tr.T, tr.R - tr.L, tr.B - tr.T));
        var work = Dip(new Rect(mi.Work.L, mi.Work.T, mi.Work.R - mi.Work.L, mi.Work.B - mi.Work.T));
        bool bottom = tr.T > (mi.Monitor.T + mi.Monitor.B) / 2;

        // the size of the search box (a search icon or a hidden box falls back to the box's usual size)
        var size = SearchFallback;
        if (_parts.Search is { Width: > 0 } sd && Dip(sd) is { Width: >= 120 } s) size = new Size(s.Width, Math.Min(s.Height, tb.Height - 4));
        double cy = tb.Top + tb.Height / 2;
        (Rect Pill, Rect Work, bool Bottom, RECT Mon, RECT Tray, bool Fits) At(double x, double w, bool fits = true) =>
            (new Rect(x, cy - size.Height / 2, w, size.Height), work, bottom, mi.Monitor, tr, fits);

        Rect? wid = _parts.Widgets is { Width: > 0 } wd ? Dip(wd) : null;
        bool widRight = wid is { } w0 && w0.Left + w0.Width / 2 > tb.Left + tb.Width / 2; // left-aligned icons put the weather on the right
        if (_parts.Apps is not { Width: > 0 } ad) // icons not read yet: beside the weather, as Windows' own search box sits
            return wid is { } w1 ? At(widRight ? w1.Left - TbGap - size.Width : w1.Right + TbGap, size.Width) : At(tb.Left + 12, size.Width);

        // Never over the icons. They spread out from the centre (or from the left) as apps open, so the pill
        // takes the free stretch beside the weather, shrinking to it; then the stretch on the icons' other side;
        // and when neither holds its content it steps off the taskbar until the icons make room again.
        var apps = Dip(ad);
        double trayLeft = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null) is var n && n != IntPtr.Zero && GetWindowRect(n, out var nr)
            ? Dip(new Rect(nr.L, nr.T, nr.R - nr.L, nr.B - nr.T)).Left : tb.Right - 200;
        double leftFrom = (wid is { } w2 && !widRight ? w2.Right : tb.Left + 4) + TbGap, leftTo = apps.Left - TbGap;
        double rightFrom = apps.Right + TbGap, rightTo = (wid is { } w3 && widRight ? w3.Left : trayLeft) - TbGap;
        double min = Math.Max(_tbRowMin, 64);
        if (!widRight && leftTo - leftFrom >= min) return At(leftFrom, Math.Min(size.Width, leftTo - leftFrom));
        if (rightTo - rightFrom >= min) { double w = Math.Min(size.Width, rightTo - rightFrom); return At(rightTo - w, w); }
        return At(leftFrom, min, fits: false);
    }

    void TaskbarTick()
    {
        if (!_taskbar || _carrying) return;
        // No taskbar for the moment (explorer restarting, a vertical one): hide and wait for it, rather than
        // leave for the top of the screen and save that as the island's place.
        var found = TaskbarSlot();
        bool hidden = true;
        if (found is { } f)
        {
            // hidden while the taskbar is (auto-hide), a full-screen app has the screen, or the icons fill it
            int visibleH = Math.Min(f.Tray.B, f.Mon.B) - Math.Max(f.Tray.T, f.Mon.T);
            hidden = visibleH < 10 || !f.Fits || FullScreenAppOn(f.Mon);
        }
        if (hidden != _tbHidden)
        {
            _tbHidden = hidden;
            if (hidden && _open && !_keyboard) Close_();
            Opacity = hidden ? 0 : 1; IsHitTestVisible = !hidden;
        }
        if (hidden) return;
        var slot = found!.Value;

        if (_pill != slot.Pill.Size) { _pill = slot.Pill.Size; FitTaskbarRow(); }
        _tbBottom = slot.Bottom;
        // the panel is centred over the pill, kept inside the work area; the window is centred on the panel
        double panelLeft = Math.Clamp(slot.Pill.Left + _pill.Width / 2 - OpenW / 2, slot.Work.Left + 12, Math.Max(slot.Work.Left + 12, slot.Work.Right - 12 - OpenW));
        double left = panelLeft - (Width - OpenW) / 2;
        double top = _tbBottom ? slot.Pill.Bottom + PillBelow - Height : slot.Pill.Top - PillBelow;
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
        _panelX = (Width - OpenW) / 2;
        _pillX = slot.Pill.Left - left;
        _pillY = _tbBottom ? Height - PillBelow - _pill.Height : PillBelow;

        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    static bool FullScreenAppOn(RECT mon)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return false;
        var cls = new StringBuilder(64); GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!(r.L <= mon.L && r.T <= mon.T && r.R >= mon.R && r.B >= mon.B)) return false;
        // A screen-sized foreground window isn't enough: Search, Start, Alt+Tab and Task View are screen-sized
        // too, and hiding for them made the pill vanish on a click. Windows' own test decides, the one it holds
        // notifications back for: busy (full screen), Direct3D full screen, or presenting.
        return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
    }

    void WatchTaskbar(bool on)
    {
        _tbWatch = on;
        if (on && _tbWatchTask is not { IsCompleted: false }) _tbWatchTask = Task.Factory.StartNew(TaskbarWatchLoop, TaskCreationOptions.LongRunning);
    }

    /// The taskbar's parts through UI Automation, off the UI thread (each read is a cross-process call, about
    /// a millisecond). The row that holds Start, Search, Task View and the app buttons is read ten times a
    /// second in one cached call, so the pill gives way as soon as the icons spread, not seconds later.
    void TaskbarWatchLoop()
    {
        AutomationElement? row = null, widgets = null;
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.AutomationIdProperty);
        TaskbarParts last = default; bool posted = false; int reads = 0;
        while (_tbWatch)
        {
            try
            {
                if (row == null || ++reads % 50 == 0) // and every ~5 s afresh: the taskbar rebuilds its buttons now and then
                {
                    var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                    var start = tray?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
                    row = start == null ? null : TreeWalker.RawViewWalker.GetParent(start); // not a control element, so it isn't found by id
                    widgets = tray?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
                }
                var parts = row == null ? default : ReadTaskbarRow(row, widgets, cache);
                if (!posted || parts != last)
                {
                    posted = true; last = parts;
                    Dispatcher.BeginInvoke(() => { _parts = parts; TaskbarTick(); if (_carrying) UpdateTaskbarGhost(CursorDip()); });
                }
                Thread.Sleep(row == null ? 1000 : 100);
            }
            catch { row = null; widgets = null; Thread.Sleep(500); } // explorer restarted, or an element went away
        }
    }

    static TaskbarParts ReadTaskbarRow(AutomationElement row, AutomationElement? widgetsEl, CacheRequest cache)
    {
        Rect? widgets = widgetsEl is { } w && !w.Current.IsOffscreen && w.Current.BoundingRectangle is { IsEmpty: false, Width: > 0 } wr ? wr : null;
        Rect? search = null, apps = null;
        AutomationElementCollection kids;
        using (cache.Activate()) kids = row.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
        foreach (AutomationElement k in kids)
        {
            var r = k.Cached.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0) continue;
            if (widgets is { } wb && r.Left < wb.Right && wb.Left < r.Right) continue; // the weather's own parts
            if (k.Cached.AutomationId == "SearchButton") search = r;
            apps = apps is { } a ? Rect.Union(a, r) : r;
        }
        return new(widgets, search, apps);
    }

    /// On the taskbar the pill is the search box's width: music sits at its left end, Claude at its right,
    /// with the space between them (14 pt in from each end, like the docked pill's padding).
    void FitTaskbarRow()
    {
        if (!_taskbar) return;
        var inf = new Size(double.PositiveInfinity, CollapsedH);
        _cutout.Width = 0;
        CollapsedRow.InvalidateMeasure(); CollapsedRow.Measure(inf); // a re-measure with the same constraint is skipped otherwise
        _tbRowMin = CollapsedRow.DesiredSize.Width + 2 * 14;
        _cutout.Width = Math.Max(0, _pill.Width - 2 * 14 - CollapsedRow.DesiredSize.Width);
        CollapsedRow.InvalidateMeasure(); CollapsedRow.Measure(inf); // a re-measure with the same constraint is skipped otherwise
    }

    // ---------------- open / close: a pop-up, not a morph ----------------
    void TaskbarOpened() { _pop.Use(PopIn.R, PopIn.D); _pop.Target = 1; _h.Snap(_openH); }
    void TaskbarClosed() { _pop.Use(PopOut.R, PopOut.D); _pop.Target = 0; _panelHClose = _h.Value; }

    Rect TaskbarPillRect() => new(_pillX - _swellW.Value / 2 + _lean.Value, _pillY - (_tbBottom ? _swellH.Value : 0), _pill.Width + _swellW.Value, _pill.Height + _swellH.Value);

    /// Hover on the taskbar: the pill opens it (a sideways sweep across the taskbar doesn't); it stays
    /// open while the pointer is on the pill, the panel, or the gap between them.
    void TrackTaskbar(Point p, Rect pill, bool sweep, bool down)
    {
        if (!_open)
        {
            var zone = pill; zone.Inflate(2, 4);
            if (zone.Contains(p) && !sweep) { Open(); return; }
            Proximity(p, pill);
            return;
        }
        var stay = Rect.Union(pill, _panelRect);
        stay.Inflate(StaySide, StayTop);
        bool dragging = down && stay.Contains(_dragFrom);
        if (!stay.Contains(p) && !_keyboard && !_hold && !dragging) Close_();
    }

    void DrawTaskbar()
    {
        var pill = TaskbarPillRect();
        double pop = Math.Clamp(_pop.Value, 0, 1.05);
        double ph = _open ? _h.Value : _panelHClose;
        double rise = (1 - pop) * PanelRise;
        double py = _tbBottom ? pill.Top - PanelGap - ph + rise : pill.Bottom + PanelGap - rise;
        _panelRect = new Rect(_panelX, py, OpenW, ph);

        var pillGeo = new RectangleGeometry(pill, pill.Height / 2, pill.Height / 2); pillGeo.Freeze();
        var panelGeo = new RectangleGeometry(_panelRect, PanelRadius, PanelRadius); panelGeo.Freeze();
        Canvas.SetLeft(Fill, 0); Canvas.SetTop(Fill, 0); Fill.Data = pillGeo;
        bool showPanel = pop > 0.005 || _open;
        PanelFill.Visibility = showPanel ? Visibility.Visible : Visibility.Hidden;
        PanelFill.Data = panelGeo; PanelFill.Opacity = Math.Min(1, pop);

        // Nonzero: where the sliding panel overlaps the pill it's still inside (the default, EvenOdd, punched a
        // hole there and cut the top off the album art); a hidden panel isn't part of the clip at all
        Geometry clip = showPanel ? new GeometryGroup { FillRule = FillRule.Nonzero, Children = { pillGeo, panelGeo } } : pillGeo;
        clip.Freeze();
        Canvas.SetLeft(Body, 0); Canvas.SetTop(Body, 0); Body.Width = Width; Body.Height = Height; Body.Clip = clip;

        // the pill keeps its content; the panel's fades in with it
        CollapsedRow.Opacity = 1; CollapsedRow.Visibility = Visibility.Visible;
        Canvas.SetLeft(CollapsedRow, pill.Left + (pill.Width - CollapsedRow.DesiredSize.Width) / 2);
        Canvas.SetTop(CollapsedRow, pill.Top + (pill.Height - CollapsedH) / 2);

        ExpandedRoot.Visibility = showPanel ? Visibility.Visible : Visibility.Hidden;
        ExpandedRoot.Opacity = Math.Clamp((pop - 0.25) / 0.6, 0, 1);
        ExpandedRoot.Height = ph;
        Canvas.SetLeft(ExpandedRoot, _panelX); Canvas.SetTop(ExpandedRoot, py);
        ExpandedRoot.IsHitTestVisible = _open && pop > 0.6;
        LayoutPages();
    }

    // ---------------- the outline shown while carrying ----------------
    void ShowTaskbarGhost()
    {
        WatchTaskbar(true);
        if (_tbGhost == null)
        {
            _tbGhostPath = new Path { Stroke = IsleStyle.White(0.55), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 3, 3 } };
            _tbGhost = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false,
                Content = new Canvas { Children = { _tbGhostPath } },
            };
            _tbGhost.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_tbGhost).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            };
        }
        _tbGhost.Opacity = 0;
        _tbGhost.Show();
        UpdateTaskbarGhost(CursorDip());
    }

    void UpdateTaskbarGhost(Point p)
    {
        if (_tbGhost is not { IsVisible: true } || _tbGhostPath == null) return;
        if (TaskbarSlot() is not { } slot) { _tbGhost.Opacity = 0; return; }
        var r = slot.Pill;
        _tbGhost.Left = r.Left - 2; _tbGhost.Top = r.Top - 2; _tbGhost.Width = r.Width + 4; _tbGhost.Height = r.Height + 4;
        _tbGhostPath.Data = new RectangleGeometry(new Rect(2, 2, r.Width, r.Height), r.Height / 2, r.Height / 2);
        bool near = NearTaskbarSlot(p);
        _tbGhostPath.Fill = new SolidColorBrush(Color.FromArgb((byte)(near ? 0xE6 : 0x59), 0, 0, 0));
        double dist = (p - new Point(r.Left + r.Width / 2, r.Top + r.Height / 2)).Length;
        _tbGhost.Opacity = near ? 1 : Math.Clamp(1 - (dist - DockReach) / 400, 0.35, 1);
    }

    /// Dropping a carried island on the taskbar, or close to its spot there, puts it on the taskbar.
    bool NearTaskbarSlot(Point dip)
    {
        if (TaskbarSlot() is not { } slot) return false;
        var tb = Dip(new Rect(slot.Tray.L, slot.Tray.T, slot.Tray.R - slot.Tray.L, slot.Tray.B - slot.Tray.T));
        tb.Inflate(0, 6);
        var c = new Point(slot.Pill.Left + slot.Pill.Width / 2, slot.Pill.Top + slot.Pill.Height / 2);
        return tb.Contains(dip) || (dip - c).Length < DockReach;
    }
}
