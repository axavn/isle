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
        menu.Items.Add("Install Claude hooks", null, (_, _) => Hooks(true));
        menu.Items.Add("Remove Claude hooks", null, (_, _) => Hooks(false));
        menu.Items.Add("Quit", null, (_, _) => Shutdown());
        _tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon()), Text = "Isle", ContextMenuStrip = menu, Visible = true };
    }

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
