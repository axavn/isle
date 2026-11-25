using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IsleWin.Claude;
using Brush = System.Windows.Media.Brush;

namespace IsleWin;

/// Isle's Pomodoro face (PomodoroExpandedView.swift): a 114 pt ring hero (raised 11) with the clock inside
/// (23 pt, 18 pt from an hour), then a 340 pt column raised 6: phase title (17), detail (12, 70%), 11 pt gap,
/// the cycle dots in a capsule, and reset / play-pause / skip. The same content box as the Music face.
public sealed class PomodoroView : Grid
{
    // arrow.counterclockwise, drawn as a stroke in a 24 box; forward.end.fill: triangle and bar
    const string Counterclockwise = "M6.4,6.4 A7.92,7.92 0 1 1 4.1,12 M6.4,6.4 V2.4 M6.4,6.4 H10.4";
    const string ForwardEnd = "M3,6.2 C3,5.4 3.9,4.9 4.6,5.4 L15,11.1 C15.6,11.5 15.6,12.5 15,12.9 L4.6,18.6 C3.9,19.1 3,18.6 3,17.8 Z M17.5,5 H19.5 A1,1 0 0 1 20.5,6 V18 A1,1 0 0 1 19.5,19 H17.5 A1,1 0 0 1 16.5,18 V6 A1,1 0 0 1 17.5,5 Z";

    readonly PomodoroService _s;
    readonly PomodoroRing _ring = PomodoroRing.Hero();
    readonly TextBlock _clock = new()
    {
        FontFamily = IsleStyle.Display, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        TextTrimming = TextTrimming.None, Margin = new Thickness(16, 0, 16, 0),
    };
    readonly TextBlock _title = new() { FontFamily = IsleStyle.Display, FontSize = IsleStyle.Title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _detail = new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Body, Foreground = IsleStyle.White(IsleStyle.Secondary), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal };
    readonly Button _reset, _play, _skip;
    readonly Path _resetIcon, _playIcon, _pauseIcon;

    public PomodoroView(PomodoroService service)
    {
        _s = service;
        var hero = new Grid { Width = 114, Height = 114, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(0, -11) };
        hero.Children.Add(_ring); hero.Children.Add(_clock);

        _resetIcon = IsleStyle.Icon(Counterclockwise, 14, Brushes.White, stroke: true, strokeWidth: 2);
        _playIcon = IsleStyle.Icon(IsleStyle.PlayFill, 22, Brushes.White);
        _pauseIcon = IsleStyle.Icon(IsleStyle.PauseFill, 22, Brushes.White);
        _reset = Control(_resetIcon, 14, _s.Reset);
        _play = Control(new Grid { Children = { _playIcon, _pauseIcon } }, 22, _s.Toggle);
        _skip = Control(IsleStyle.Icon(ForwardEnd, 14, Brushes.White), 14, _s.Skip);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(_reset); controls.Children.Add(_play); controls.Children.Add(_skip);
        _play.Margin = new Thickness(14, 0, 14, 0);

        var capsule = new Border
        {
            CornerRadius = new CornerRadius(100), Background = IsleStyle.White(0.12), Padding = new Thickness(8, 6, 8, 6),
            Child = _dots, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
        row.Children.Add(capsule); row.Children.Add(controls);

        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(_title); inner.Children.Add(_detail); inner.Children.Add(row);
        var column = new Grid { Width = 340, Margin = new Thickness(14, 0, 0, 0), RenderTransform = new TranslateTransform(0, -6) };
        column.Children.Add(inner);

        var layout = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        layout.Children.Add(hero); layout.Children.Add(column);
        Children.Add(layout);

        _s.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    static Button Control(UIElement glyph, double size, Action click)
    {
        var b = new Button { Width = size + 7, Height = size + 7, Focusable = false, Cursor = System.Windows.Input.Cursors.Hand, Content = glyph, Template = Bare() };
        b.Click += (_, _) => click();
        HoverGrow.Attach(b, press: true);
        return b;
    }

    static ControlTemplate Bare()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        f.AppendChild(cp);
        t.VisualTree = f;
        return t;
    }

    /// The Home tile's lead icon: SF `timer` as Segoe Fluent's stopwatch, white, semibold-ish (size * 0.85).
    public static UIElement TileIcon(double size = 18) => IsleStyle.Glyph("", size * 0.85, Brushes.White);

    void Refresh()
    {
        Brush tint = _s.Tint;
        _ring.Tint = tint; _ring.Progress = _s.Progress;
        _clock.Text = _s.ClockText; _clock.FontSize = _s.IsLongClock ? 18 : 23;
        _title.Text = _s.Title; _detail.Text = _s.Detail;

        int total = _s.SessionsPerCycle;
        if (_dots.Children.Count != total)
        {
            _dots.Children.Clear();
            for (int i = 0; i < total; i++)
                _dots.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) });
        }
        for (int i = 0; i < total; i++)
            ((Ellipse)_dots.Children[i]).Fill = i < _s.CycleIndex ? tint : IsleStyle.White(0.22);
        _dots.ToolTip = $"{_s.CycleIndex} of {total} focus sessions done";

        _reset.IsEnabled = _s.IsActive;
        _resetIcon.Stroke = _s.IsActive ? Brushes.White : IsleStyle.White(IsleStyle.Faint);
        _playIcon.Visibility = _s.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        _pauseIcon.Visibility = _s.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }
}
