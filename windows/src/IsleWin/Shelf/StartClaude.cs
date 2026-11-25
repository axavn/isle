using System.IO;
using System.ComponentModel;
using System.Diagnostics;

namespace IsleWin.Shelf;

/// "Start Claude here": a new terminal window in the folder running `claude` (Isle opens Terminal.app).
/// Windows Terminal when it's installed, else a plain console window. Runs off the UI thread.
public static class StartClaude
{
    /// True when the folder is there. Call from a worker (a dead share would block).
    public static bool CanStart(string folder) => !string.IsNullOrEmpty(folder) && Directory.Exists(folder);

    public static void Open(string folder) => Task.Run(() =>
    {
        if (!CanStart(folder)) return;
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[] { "-w", "new", "-d", folder, "cmd.exe", "/k", "claude" }) wt.ArgumentList.Add(a);
            Process.Start(wt)?.Dispose();
        }
        catch (Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe", "/k claude") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose(); } catch { }
        }
        catch { }
    });
}
