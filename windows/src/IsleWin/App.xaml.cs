using System.IO;
using System.Threading;
using System.Windows;
using IsleWin.Core;

namespace IsleWin;

public partial class App : Application
{
    Mutex? _mutex;
    System.Windows.Forms.NotifyIcon? _tray;
    IslandWindow? _window;

    public static void Log(object? o)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".isle");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "islewin.log"), $"{DateTime.Now:O} {o}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _mutex = new Mutex(true, "IsleWin.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        _window = new IslandWindow();
        _window.Show();
        MakeTray();
    }

    void MakeTray()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(System.Drawing.Brushes.Black, 1, 8, 30, 16);
            g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x5D, 0xCA, 0xA5)), 21, 13, 6, 6);
        }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show/Hide island", null, (_, _) => { if (_window!.IsVisible) _window.Hide(); else _window.Show(); });
        menu.Items.Add("Place on taskbar", null, (_, _) => _window!.PlaceOnTaskbar());
        menu.Items.Add("Place at top of screen", null, (_, _) => _window!.PlaceAtTop());
        menu.Items.Add("Install Claude hooks", null, (_, _) => Hooks(true));
        menu.Items.Add("Remove Claude hooks", null, (_, _) => Hooks(false));
        // Isle's Settings, until the port has a Settings window: Pomodoro is opt-in; each sound is a switch, and
        // switching one on plays it, as his sound rows audition by ear
        var settings = IsleSettings.Current;
        var pomodoro = new System.Windows.Forms.ToolStripMenuItem("Pomodoro") { CheckOnClick = true, Checked = settings.Pomodoro.Enabled };
        pomodoro.CheckedChanged += (_, _) => { settings.Pomodoro.Enabled = pomodoro.Checked; settings.Save(); };
        menu.Items.Add(pomodoro);
        var sounds = new System.Windows.Forms.ToolStripMenuItem("Sounds");
        void Sound(string title, Func<bool> get, Action<bool> set, Func<string> name)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(title) { CheckOnClick = true, Checked = get() };
            item.CheckedChanged += (_, _) => { set(item.Checked); settings.Save(); if (item.Checked) Media.Sounds.Play(name()); };
            sounds.DropDownItems.Add(item);
        }
        Sound("Claude alert", () => settings.Sounds.ClaudeAlertEnabled, v => settings.Sounds.ClaudeAlertEnabled = v, () => settings.Sounds.ClaudeAlert);
        Sound("Claude done", () => settings.Sounds.ClaudeDoneEnabled, v => settings.Sounds.ClaudeDoneEnabled = v, () => settings.Sounds.ClaudeDone);
        Sound("When a Pomodoro interval ends", () => settings.Pomodoro.SoundEnabled, v => settings.Pomodoro.SoundEnabled = v, () => settings.Pomodoro.Sound);
        menu.Items.Add(sounds);
        menu.Items.Add("Connect Spotify…", null, (_, _) => { if (_spotify is { IsVisible: true }) _spotify.Activate(); else (_spotify = new SpotifyConnectWindow(_window!.Media.Web)).Show(); });
        menu.Items.Add("Quit", null, (_, _) => Shutdown());
        _tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon()), Text = "Isle", ContextMenuStrip = menu, Visible = true };
        // the Web API's problems the user can fix (Premium needed, sign-in expired), as a tray note
        _window!.Media.Web.Problem += m => Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(4000, "Isle", m, System.Windows.Forms.ToolTipIcon.Info));
    }

    SpotifyConnectWindow? _spotify;

    void Hooks(bool install)
    {
        string msg;
        try
        {
            if (install) HookInstaller.Install(IslePaths.Default, AppContext.BaseDirectory); else HookInstaller.Uninstall(IslePaths.Default);
            msg = install ? "Claude hooks installed." : "Claude hooks removed.";
        }
        catch (Exception ex) { Log(ex); msg = "Failed: " + ex.Message; }
        _tray?.ShowBalloonTip(3000, "Isle", msg, System.Windows.Forms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _window?.Claude.Stop(); } catch (Exception ex) { Log(ex); }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
