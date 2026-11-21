using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IsleWin.Claude;

public enum PasteResult { Sent, NeedsConfirm, NoWindow }

/// Talks to the console a Claude CLI process runs in.
///
/// Replies are typed straight into that console's input buffer (AttachConsole + WriteConsoleInput),
/// so they reach the right session even if its terminal tab is in the background, minimized, or
/// another app has focus. No clipboard, no focus stealing, no window-title guessing.
public static class TerminalBridge
{
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;   // 1 = KEY_EVENT
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public char UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] buf, uint len, out uint written);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    static readonly object _consoleLock = new(); // AttachConsole is process-wide: one at a time

    /// Runs `work` while attached to `pid`'s console. Returns false if we couldn't attach.
    static bool WithConsole(int pid, Action work)
    {
        lock (_consoleLock)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;
            SetConsoleCtrlHandler(IntPtr.Zero, true); // don't let a Ctrl+C in that console reach us
            try { work(); return true; }
            finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
        }
    }

    /// The terminal window showing this pid's console: Windows Terminal / VS Code own the ConPTY
    /// pseudo-window, classic conhost shows the console window itself.
    public static IntPtr FindHostWindow(int pid)
    {
        if (pid <= 0) return IntPtr.Zero;
        IntPtr host = IntPtr.Zero;
        try { WithConsole(pid, () => { var c = GetConsoleWindow(); if (c != IntPtr.Zero) host = GetAncestor(c, 3 /* GA_ROOTOWNER */); }); }
        catch { }
        return host;
    }

    public static bool Jump(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        return SetForegroundWindow(hwnd);
    }

    /// True if ~/.claude/sessions/&lt;pid&gt;.json says it was started by the desktop app (no terminal to type into).
    public static bool IsDesktopSession(string claudeSessionsDir, int pid)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeSessionsDir, pid + ".json")));
            return doc.RootElement.TryGetProperty("entrypoint", out var e) && e.GetString() is { } s && s.Contains("desktop", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CSBI { public short SizeX, SizeY, CurX, CurY; public ushort Attr; public short Left, Top, Right, Bottom, MaxX, MaxY; }
    [DllImport("kernel32.dll")] static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CSBI info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool ReadConsoleOutputCharacterW(IntPtr h, System.Text.StringBuilder sb, uint len, int coord, out uint read);

    // Claude Code's status line, e.g. "✽ Germinating… (16s · ↓ 1.6k tokens)"
    static readonly System.Text.RegularExpressions.Regex SpinnerLine = new(@"^\s*[·✢✳✶✻✽*]\s+(\p{L}[\p{L}'\- ]{1,30}?)…");

    /// The word Claude Code's spinner is showing right now ("Germinating"), read off the
    /// console's visible screen; null if no spinner line is on screen.
    public static string? ReadSpinnerVerb(int pid)
    {
        if (pid <= 0) return null;
        string? verb = null;
        try
        {
            WithConsole(pid, () =>
            {
                var h = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) return;
                try
                {
                    if (!GetConsoleScreenBufferInfo(h, out var i)) return;
                    int w = i.SizeX;
                    var sb = new System.Text.StringBuilder(w);
                    for (int y = i.Bottom; y >= i.Top && verb == null; y--) // spinner sits near the bottom
                    {
                        sb.Clear();
                        if (!ReadConsoleOutputCharacterW(h, sb, (uint)w, y << 16, out var n)) continue;
                        var m = SpinnerLine.Match(sb.ToString(0, Math.Min((int)n, sb.Length)));
                        if (m.Success) verb = m.Groups[1].Value.Trim();
                    }
                }
                finally { CloseHandle(h); }
            });
        }
        catch { }
        return verb;
    }

    static INPUT_RECORD Key(bool down, char ch, ushort vk = 0) =>
        new() { EventType = 1, KeyDown = down ? 1 : 0, RepeatCount = 1, VirtualKeyCode = vk, UnicodeChar = ch };

    static bool Write(INPUT_RECORD[] recs)
    {
        // CONIN$ = the attached console's input buffer (works for ConPTY too)
        var h = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return false;
        try { return WriteConsoleInputW(h, recs, (uint)recs.Length, out var n) && n == recs.Length; }
        finally { CloseHandle(h); }
    }

    /// Types `text` into the Claude process's console and presses Enter.
    /// (Signature kept for callers; sessionName/project/force are no longer needed.)
    public static async Task<PasteResult> PasteReply(int pid, string text, string? sessionName, string? project, bool force = false)
    {
        // single-line box: a newline would submit early, so fold them into spaces
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        var chars = new List<INPUT_RECORD>(text.Length * 2);
        foreach (var c in text) { chars.Add(Key(true, c)); chars.Add(Key(false, c)); }

        bool ok = false;
        bool attached = await Task.Run(() => WithConsole(pid, () => ok = Write(chars.ToArray())));
        if (!attached || !ok) return PasteResult.NoWindow;
        // Enter goes separately so the TUI sees the text land first instead of treating it as one pasted blob.
        await Task.Delay(150);
        attached = await Task.Run(() => WithConsole(pid, () => ok = Write(new[] { Key(true, '\r', 0x0D), Key(false, '\r', 0x0D) })));
        return attached && ok ? PasteResult.Sent : PasteResult.NoWindow;
    }
}
