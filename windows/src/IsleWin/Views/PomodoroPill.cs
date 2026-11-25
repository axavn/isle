using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IsleWin.Claude;

namespace IsleWin;

/// Isle's collapsed-pill Pomodoro. Cluster (`Compact`, beside Claude or music): a 16 pt ring (stroke 2), a 5 pt
/// gap, then the clock. Alone, the pill puts the ring (22 pt, stroke 2.5) left of the camera gap and the clock
/// right of it, so build two parts with `Part.RingOnly` and `Part.ClockOnly`. The clock is 10 pt semibold
/// tabular digits in the phase tint, left-aligned in a slot fixed to the interval's widest text
/// (template width + 2, rounded up), so ticking digits never move the pill. The ring is 100% while running
/// and 55% paused (0.2 s).
public sealed class PomodoroPill : StackPanel
{
    public enum Part { Cluster, RingOnly, ClockOnly }

    readonly PomodoroService _s;
    readonly Part _part;
    readonly PomodoroRing? _ring;
    readonly TextBlock? _clock;
    bool _wasRunning;

    public PomodoroPill(PomodoroService service, Part part = Part.Cluster)
    {
        _s = service; _part = part;
        Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center;
        if (part != Part.ClockOnly)
        {
            _ring = part == Part.RingOnly ? PomodoroRing.Alone() : PomodoroRing.Small();
            Children.Add(_ring);
        }
        if (part != Part.RingOnly)
        {
            _clock = new TextBlock
            {
                FontFamily = IsleStyle.Text, FontSize = 10, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
                TextWrapping = TextWrapping.NoWrap,
            };
            if (part == Part.Cluster) _clock.Margin = new Thickness(5, 0, 0, 0);
            Children.Add(_clock);
        }
        _wasRunning = _s.IsRunning;
        if (_ring != null && !_wasRunning) _ring.Opacity = 0.55;
        Loaded += (_, _) => { _s.PropertyChanged += OnChanged; Refresh(); };
        Unloaded += (_, _) => _s.PropertyChanged -= OnChanged;
        Refresh();
    }

    /// The width the clock slot needs now (template width + 2, rounded up); the window can use it to size the pill.
    public double ClockSlotWidth => _clock == null ? 0 : Slot(_s.ClockTemplate);

    static double Slot(string template)
    {
        var t = new TextBlock
        {
            Text = template, FontFamily = IsleStyle.Text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(t.DesiredSize.Width) + 2;
    }

    /// Total width of this part (for pill layout): ring, 5 pt gap and the slot, as built.
    public double ContentWidth => (_ring?.Width ?? 0) + (_clock == null ? 0 : (_part == Part.Cluster ? 5 : 0) + ClockSlotWidth);

    void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    void Refresh()
    {
        var tint = _s.Tint;
        if (_ring != null)
        {
            _ring.Tint = tint; _ring.Progress = _s.Progress;
            if (_s.IsRunning != _wasRunning)
            {
                _wasRunning = _s.IsRunning;
                _ring.BeginAnimation(OpacityProperty, new DoubleAnimation(_wasRunning ? 1 : 0.55, TimeSpan.FromSeconds(0.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            }
        }
        if (_clock != null)
        {
            _clock.Foreground = tint; _clock.Text = _s.ClockText; _clock.Width = ClockSlotWidth;
        }
    }
}
