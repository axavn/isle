using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IsleWin.Claude;
using IsleWin.Core;
using IsleWin.Media;

namespace IsleWin;

/// Isle's Home: a row of tiles (radius 14, white 7%), one per face, each with a 24 pt lead
/// (the album / the Claude marker), the face's name, and a two-line glance. Click to open the face.
public sealed class HomeView : Grid
{
    const double Gap = 8, Inset = 2, Lead = 24;
    readonly Border _musicTile, _claudeTile;
    readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    readonly Border _artBox;
    readonly DotMatrix _dots = new() { Width = Lead, Height = Lead, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _m1 = Line1(), _m2 = Line2(), _c1 = Line1(), _c2 = Line2();
    readonly MediaService _media;
    readonly ClaudeViewModel _claude;

    public event Action<IslandFace>? Picked;

    public HomeView(MediaService media, ClaudeViewModel claude)
    {
        _media = media; _claude = claude;
        Margin = new Thickness(0, Inset, 0, Inset);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
        ColumnDefinitions.Add(new ColumnDefinition());

        _artBox = new Border { Width = Lead, Height = Lead, Child = _art, HorizontalAlignment = HorizontalAlignment.Left, Clip = new RectangleGeometry(new Rect(0, 0, Lead, Lead), 5, 5) };
        _musicTile = Tile(_artBox, "Music", _m1, _m2, IslandFace.Music);
        _claudeTile = Tile(_dots, "Claude", _c1, _c2, IslandFace.Claude);
        SetColumn(_claudeTile, 2);
        Children.Add(_musicTile); Children.Add(_claudeTile);

        media.PropertyChanged += (_, _) => Refresh();
        claude.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    static TextBlock Line1() => new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Body, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    static TextBlock Line2() => new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Caption, FontWeight = FontWeights.Medium, Foreground = IsleStyle.White(IsleStyle.Secondary), TextTrimming = TextTrimming.CharacterEllipsis };

    Border Tile(FrameworkElement lead, string title, TextBlock l1, TextBlock l2, IslandFace face)
    {
        var body = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(lead, Dock.Top);
        body.Children.Add(lead);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontFamily = IsleStyle.Text, FontSize = IsleStyle.Caption, FontWeight = FontWeights.SemiBold, Foreground = IsleStyle.White(IsleStyle.MetaTone) });
        text.Children.Add(l1); text.Children.Add(l2);
        DockPanel.SetDock(text, Dock.Bottom);
        body.Children.Add(text);
        var tile = new Border { CornerRadius = new CornerRadius(14), Background = IsleStyle.White(0.07), Padding = new Thickness(9), Child = body, Cursor = Cursors.Hand };
        tile.MouseLeftButtonUp += (_, _) => Picked?.Invoke(face);
        tile.MouseEnter += (_, _) => tile.Background = IsleStyle.White(0.10);
        tile.MouseLeave += (_, _) => Refresh();
        return tile;
    }

    void Refresh()
    {
        _art.Source = _media.Artwork;
        _artBox.Background = new LinearGradientBrush(_media.Palette.Primary, _media.Palette.Secondary, 45);
        _m1.Text = _media.HasTrack ? _media.Title : "Nothing playing";
        _m2.Text = _media.HasTrack ? _media.Artist : "";

        _dots.State = _claude.MarkerState;
        _dots.Primary = _claude.PalettePrimary; _dots.Secondary = _claude.PaletteSecondary; _dots.Accent = _claude.PaletteAccent;
        _c1.Text = _claude.HasSession ? _claude.CollapsedWord.TrimEnd('…') : "No session";
        _c2.Text = !_claude.HasSession ? "" : _claude.HasAgents ? _claude.AgentsLine : _claude.SessionsLine;

        // a tile that needs you is lit a little brighter
        if (!_musicTile.IsMouseOver) _musicTile.Background = IsleStyle.White(0.07);
        if (!_claudeTile.IsMouseOver) _claudeTile.Background = IsleStyle.White(_claude.NeedsAttention ? 0.12 : 0.07);
    }
}

/// The way home from inside a face, top-right in the band: ⊞ › Music.
public sealed class Crumb : StackPanel
{
    readonly TextBlock _title = new() { FontFamily = IsleStyle.Text, FontSize = IsleStyle.Meta, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    public event Action? Home;

    public Crumb()
    {
        Orientation = Orientation.Horizontal; Height = 22; Cursor = Cursors.Hand; Background = Brushes.Transparent;
        var grid = IsleStyle.Icon(IsleStyle.Grid2x2Fill, 11, Brushes.White);
        var chev = IsleStyle.Icon(IsleStyle.ChevronRight, 7, IsleStyle.White(IsleStyle.MetaTone), stroke: true, strokeWidth: 3.2);
        chev.Margin = new Thickness(6, 0, 6, 0);
        grid.Margin = new Thickness(4, 0, 0, 0);
        Children.Add(grid); Children.Add(chev); Children.Add(_title);
        MouseLeftButtonUp += (_, _) => Home?.Invoke();
        MouseEnter += (_, _) => Opacity = 0.8; MouseLeave += (_, _) => Opacity = 1;
    }

    public string Title { set => _title.Text = value; }
}
