using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IsleWin.Claude;
using IsleWin.Media;
using Windows.Media;
using Brush = System.Windows.Media.Brush;
using Path = System.Windows.Shapes.Path;

namespace IsleWin;

/// Isle's (macOS) open music face: 114 pt art (reaching up into the band), a 340 pt column with
/// the scrolling title and artist, a 6 pt white scrubber with elapsed / -remaining, plain white
/// SF-style transport controls, and the equalizer in the top-right corner.
public sealed class MusicView : Grid
{
    readonly MediaService _m;
    readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    readonly Border _artBox;
    readonly Marquee _title = new(IsleStyle.Headline, FontWeights.SemiBold, Brushes.White, 17);
    readonly Marquee _artist = new(IsleStyle.Meta, FontWeights.Medium, IsleStyle.White(IsleStyle.Secondary), 15);
    readonly Grid _bar = new() { Height = 10, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly Border _fill = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly TextBlock _elapsed = Time(), _remaining = Time();
    readonly StackPanel _scrubber = new();
    readonly Path _play = IsleStyle.Icon(IsleStyle.PlayFill, 28, Brushes.White), _pause = IsleStyle.Icon(IsleStyle.PauseFill, 28, Brushes.White);
    readonly Button _shuffle, _repeat, _playBtn;
    readonly TextBlock _repeatGlyph = IsleStyle.Glyph(IsleStyle.Repeat, 15, Brushes.White), _shuffleGlyph = IsleStyle.Glyph(IsleStyle.Shuffle, 15, Brushes.White);
    readonly StackPanel _playing = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _idle = new() { VerticalAlignment = VerticalAlignment.Center };
    public Equalizer Wave { get; } = new() { Width = 30, Height = 24 };
    bool _drag; double _frac;

    static TextBlock Time() => new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Micro, FontWeight = FontWeights.Medium, Foreground = IsleStyle.White(IsleStyle.MetaTone), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };

    public MusicView(MediaService media)
    {
        _m = media;
        // art: 114, radius 14, nudged up 11 into the band
        _artBox = new Border
        {
            Width = 114, Height = 114, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Child = _art, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(0, -11),
        };
        _artBox.Clip = new RectangleGeometry(new Rect(0, 0, 114, 114), 14, 14);

        // scrubber
        _bar.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = IsleStyle.White(0.22) });
        _bar.Children.Add(_fill);
        _bar.MouseLeftButtonDown += (_, e) => { if (_m.Duration <= TimeSpan.Zero) return; _drag = true; _bar.CaptureMouse(); SetFrac(e); };
        _bar.MouseMove += (_, e) => { if (_drag) SetFrac(e); };
        _bar.MouseLeftButtonUp += (_, e) => { if (!_drag) return; SetFrac(e); _drag = false; _bar.ReleaseMouseCapture(); _m.Seek(_m.Duration * _frac); Tick(); };
        _bar.SizeChanged += (_, _) => Tick();
        var times = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(_remaining, Dock.Right);
        times.Children.Add(_remaining); times.Children.Add(_elapsed);
        _scrubber.Children.Add(_bar); _scrubber.Children.Add(times);
        _scrubber.RenderTransform = new TranslateTransform(0, 3);

        // controls: 16 apart, each glyph in a (size + 7) square
        _shuffle = Control(_shuffleGlyph, 15, () => _m.ToggleShuffle());
        _playBtn = Control(new Grid { Children = { _play, _pause } }, 28, () => _m.PlayPause());
        _repeat = Control(_repeatGlyph, 15, () => _m.CycleRepeat());
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(_shuffle);
        controls.Children.Add(Control(IsleStyle.Icon(IsleStyle.BackwardFill, 21, Brushes.White), 21, () => _m.Previous()));
        controls.Children.Add(_playBtn);
        controls.Children.Add(Control(IsleStyle.Icon(IsleStyle.ForwardFill, 21, Brushes.White), 21, () => _m.Next()));
        controls.Children.Add(_repeat);
        for (int i = 0; i < controls.Children.Count - 1; i++) ((FrameworkElement)controls.Children[i]).Margin = new Thickness(0, 0, 16, 0);

        // column: labels / 6 / scrubber / 2 / controls  (right inset leaves room for the corner waveform)
        var labels = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
        labels.Children.Add(_title); labels.Children.Add(_artist); _artist.Margin = new Thickness(0, 1, 0, 0);
        _playing.Children.Add(labels);
        _scrubber.Margin = new Thickness(0, 6, 0, 0); _playing.Children.Add(_scrubber);
        controls.Margin = new Thickness(0, 2, 0, 0); _playing.Children.Add(controls);

        _idle.Children.Add(new TextBlock { Text = "Nothing playing", FontFamily = IsleStyle.Display, FontSize = IsleStyle.Title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        _idle.Children.Add(new TextBlock { Text = "Start something in Spotify or another player", FontFamily = IsleStyle.Text, FontSize = IsleStyle.Body, Foreground = IsleStyle.White(IsleStyle.Secondary), Margin = new Thickness(0, 2, 0, 0) });

        var column = new Grid { Width = 340, Margin = new Thickness(14, 0, 0, 0) };
        column.RenderTransform = new TranslateTransform(0, -1);
        column.Children.Add(_playing); column.Children.Add(_idle);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(_artBox); row.Children.Add(column);
        Children.Add(row);

        // corner waveform: 30×24, 10 from the top, 4 from the right
        Wave.HorizontalAlignment = HorizontalAlignment.Right; Wave.VerticalAlignment = VerticalAlignment.Top;
        Wave.Margin = new Thickness(0, 10, 4, 0);
        Children.Add(Wave);

        media.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    static Button Control(UIElement glyph, double size, Action click)
    {
        var b = new Button
        {
            Width = size + 7, Height = size + 7, Focusable = false, Cursor = Cursors.Hand, Content = glyph,
            Template = Bare(),
        };
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

    void Refresh()
    {
        _art.Source = _m.Artwork;
        if (_m.Artwork == null)
            _artBox.Background = new LinearGradientBrush(_m.Palette.Primary, _m.Palette.Secondary, 45);
        Wave.SetPalette(_m.Palette);
        if (!_m.IsPlaying) Wave.SetLevels(null);
        _playing.Visibility = _m.HasTrack ? Visibility.Visible : Visibility.Collapsed;
        _idle.Visibility = _m.HasTrack ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = string.IsNullOrEmpty(_m.Title) ? "Not playing" : _m.Title;
        _artist.Text = _m.Artist;
        _play.Visibility = _m.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        _pause.Visibility = _m.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        // shuffle / repeat: white when on, 35% when off; shown only where the player supports them
        bool showSR = _m.ShuffleEnabled || _m.RepeatEnabled;
        _shuffle.Visibility = _repeat.Visibility = showSR ? Visibility.Visible : Visibility.Collapsed;
        _shuffleGlyph.Opacity = _m.Shuffle ? 1 : IsleStyle.Faint;
        _repeatGlyph.Opacity = _m.Repeat != MediaPlaybackAutoRepeatMode.None ? 1 : IsleStyle.Faint;
        _repeatGlyph.Text = _m.Repeat == MediaPlaybackAutoRepeatMode.Track ? IsleStyle.RepeatOne : IsleStyle.Repeat;
        _scrubber.Opacity = _m.Duration > TimeSpan.Zero ? 1 : 0.4;
        Tick();
    }

    /// 30 Hz while open and playing, and on every media change.
    public void Tick()
    {
        var dur = _m.Duration;
        double f = _drag ? _frac : dur > TimeSpan.Zero ? Math.Clamp(_m.GetPosition() / dur, 0, 1) : 0;
        _fill.Width = Math.Max(0, _bar.ActualWidth * f);
        var pos = _drag ? dur * f : _m.GetPosition();
        _elapsed.Text = Fmt(pos);
        _remaining.Text = "-" + Fmt(dur > pos ? dur - pos : TimeSpan.Zero);
    }

    public void SetLevels(float[]? v) => Wave.SetLevels(v);

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    void SetFrac(MouseEventArgs e) { _frac = Math.Clamp(e.GetPosition(_bar).X / Math.Max(1, _bar.ActualWidth), 0, 1); Tick(); }
}
