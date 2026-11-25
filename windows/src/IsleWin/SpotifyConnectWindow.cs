using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using IsleWin.Core;
using IsleWin.Media;

namespace IsleWin;

/// "Connect Spotify…" from the tray: the Web API shows and controls Spotify playing on other devices.
/// Spotify only lets an app in development mode serve its owner and 5 people, so each person makes their
/// own (free, a couple of minutes) and pastes its Client ID here.
public sealed class SpotifyConnectWindow : Window
{
    const string Dashboard = "https://developer.spotify.com/dashboard";
    readonly SpotifyWeb _web;
    readonly TextBox _id = new() { Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
    readonly TextBlock _status = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly Button _connect = new() { Content = "Connect", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
    readonly Button _disconnect = new() { Content = "Disconnect", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };

    public SpotifyConnectWindow(SpotifyWeb web)
    {
        _web = web;
        Title = "Isle: Spotify on other devices";
        Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;

        var steps = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        steps.Inlines.Add(new Run("Shows what your Spotify account plays on your phone, a speaker or another computer, and controls it (Premium). Music on this PC keeps coming from Windows, as before.\n\n"));
        steps.Inlines.Add(new Run("1. Open the ")); var link = new Hyperlink(new Run("Spotify developer dashboard")) { NavigateUri = new Uri(Dashboard) };
        link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        steps.Inlines.Add(link); steps.Inlines.Add(new Run(" and create an app (Premium is needed to own one).\n"));
        steps.Inlines.Add(new Run("2. Redirect URI: ")); steps.Inlines.Add(new Run(SpotifyApi.RedirectUri) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        steps.Inlines.Add(new Run("\n3. Under APIs used, tick Web API. Save.\n4. Copy the app's Client ID here:"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_connect); buttons.Children.Add(_disconnect);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(steps); root.Children.Add(_id); root.Children.Add(_status); root.Children.Add(buttons);
        Content = root;

        _id.Text = web.ClientId ?? "";
        _connect.Click += async (_, _) => await Connect();
        _disconnect.Click += (_, _) => { _web.Disconnect(); Show_("Disconnected."); };
        Show_(web.IsConnected ? "Connected." : "");
    }

    void Show_(string status)
    {
        _status.Text = status;
        _status.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _disconnect.IsEnabled = _web.IsConnected;
    }

    async Task Connect()
    {
        var id = _id.Text.Trim();
        if (id.Length < 16) { Show_("That doesn't look like a Client ID: it's the 32-character code on the app's page."); return; }
        _connect.IsEnabled = false;
        Show_("Finish signing in to Spotify in your browser…");
        bool ok = false;
        try { ok = await _web.ConnectAsync(id); }
        catch (Exception e) { App.Log(e); }
        _connect.IsEnabled = true;
        Show_(ok ? "Connected. Play something on your phone to see it on the island." :
                   "Didn't connect. Check the redirect URI matches exactly, then try again.");
    }
}
