using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IsleWin.Claude;
using IsleWin.Core;

namespace IsleWin;

/// Toasts on the collapsed pill (IslandToast.swift): a toast borrows the whole pill — its glyph where the art
/// sits, its words on the other side — for 4 s, and the pill resizes to it on the ordinary width spring.
/// - A toast of the same kind replaces the one showing (and restarts its 4 s); others queue, 2 at most (the
///   oldest waiting one is dropped).
/// - A live Claude alert (approval, question) drops new toasts and hides the one showing; its time runs out
///   unseen, as Isle's does.
/// - Kind "shortcut" (what a keyboard decision says) has a slot of its own: 2.5 s, never queued, shown even
///   over a live alert — it's the answer to that alert.
/// - Content cross-fades, 0.25 s ease-in-out. Nothing is drawn in the open panel.
public partial class IslandWindow
{
    const double ToastSeconds = 4, NoticeSeconds = 2.5, ToastFade = 0.25;
    static readonly Color Neutral = Color.FromRgb(0xEB, 0xEB, 0xEB); // Color(white: 0.92): no colour of its own

    IslandToast? _toast, _notice, _drawnToast;
    readonly List<IslandToast> _toastQueue = new();
    DispatcherTimer? _toastTimer, _noticeTimer;
    double _fadeFrom = -1; bool _fadeSwapPending;

    public void Toast(IslandToast toast) => UI(() => ShowToast(toast));

    bool LiveAlert => Claude.NeedsAttention;
    IslandToast? ShownToast() => _notice ?? (LiveAlert ? null : _toast);

    void ShowToast(IslandToast t)
    {
        if (t.Kind == "shortcut")
        {
            _notice = t;
            Restart(ref _noticeTimer, NoticeSeconds, () => { _notice = null; ToastChanged(); });
            ToastChanged();
            return;
        }
        if (LiveAlert) return; // dropped, not deferred
        if (_toast?.Kind == t.Kind) { Present(t); return; }
        _toastQueue.RemoveAll(q => q.Kind == t.Kind);
        if (_toast == null) { Present(t); return; }
        _toastQueue.Add(t);
        if (_toastQueue.Count > 2) _toastQueue.RemoveAt(0);
    }

    void Present(IslandToast t)
    {
        _toast = t;
        Restart(ref _toastTimer, ToastSeconds, AdvanceToast);
        ToastChanged();
    }

    void AdvanceToast()
    {
        if (_toastQueue.Count == 0) { _toast = null; _toastTimer?.Stop(); ToastChanged(); return; }
        var next = _toastQueue[0]; _toastQueue.RemoveAt(0);
        Present(next);
    }

    void Restart(ref DispatcherTimer? timer, double seconds, Action done)
    {
        timer?.Stop();
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); done(); };
        t.Start();
        timer = t;
    }

    /// What the pill shows may have changed (a toast came or went, or an alert hid one): cross-fade to it.
    void ToastChanged()
    {
        if (Equals(ShownToast(), _drawnToast)) return;
        _fadeFrom = _clock.Elapsed.TotalSeconds; _fadeSwapPending = true;
    }

    /// Per frame: swap the content at the fade's midpoint; the pill's content alpha for this frame.
    double ToastFadeAlpha(double t)
    {
        if (_fadeFrom < 0) return 1;
        double half = ToastFade / 2, k = t - _fadeFrom;
        if (_fadeSwapPending && k >= half) { _fadeSwapPending = false; _drawnToast = ShownToast(); RebuildCollapsed(); }
        if (k >= ToastFade) { _fadeFrom = -1; return 1; }
        double x = k < half ? 1 - k / half : (k - half) / half;
        return x * x * (3 - 2 * x); // ease in-out
    }

    /// The toast's pill: the glyph in the art's slot (12 pt in 22 × 18) on the left, the words (10 pt semibold)
    /// on the right, both in the toast's tint. Off the notch (floating, taskbar) the glyph slot is 24 and the
    /// words 12 pt, 8 apart, as Isle's floating capsule has them.
    void BuildToastRow(IslandToast t)
    {
        var tint = new SolidColorBrush(t.Tint is { } hex && ColorConverter.ConvertFromString(hex) is Color c ? c : Neutral); tint.Freeze();
        bool capsule = _floating || _taskbar;
        var glyph = IsleStyle.Glyph(t.Glyph, 12, tint);
        glyph.FontWeight = FontWeights.SemiBold; glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var slot = new Grid { Width = capsule ? 24 : Album, Height = 18, VerticalAlignment = VerticalAlignment.Center, Children = { glyph } };
        var words = new TextBlock { Text = t.Text, FontFamily = IsleStyle.Text, FontSize = capsule ? 12 : 10, FontWeight = FontWeights.SemiBold, Foreground = tint, VerticalAlignment = VerticalAlignment.Center };
        _lead.Children.Add(slot); _trail.Children.Add(words);
        if (capsule) { _lead.Margin = new Thickness(0, 0, 4, 0); _trail.Margin = new Thickness(4, 0, 0, 0); }
    }
}
