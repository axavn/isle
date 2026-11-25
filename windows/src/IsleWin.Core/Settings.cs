using System.Text.Json;
using System.Text.Json.Serialization;

namespace IsleWin.Core;

/// Isle's settings (AppSettings.swift), the ones the Windows port has so far, in `~/.isle/settings.json`.
/// Keys keep the macOS names. Defaults are his: Pomodoro off (it's opt-in), its fanfare on; the Claude
/// alert and done sounds off. There's no Settings window yet: the tray menu edits these.
public sealed class IsleSettings
{
    public PomodoroSettings Pomodoro { get; set; } = new();
    public SoundSettings Sounds { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static IsleSettings? _current;
    [JsonIgnore] public string? Path { get; private set; }

    /// Raised (on the saving thread) after any Save.
    public static event Action? Changed;

    public static IsleSettings Current => _current ??= Load(System.IO.Path.Combine(IslePaths.Default.Root, "settings.json"));

    public static IsleSettings Load(string path)
    {
        IsleSettings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<IsleSettings>(AtomicFile.ReadAllText(path), Json) ?? new() : new(); }
        catch { s = new(); } // a broken file falls back to the defaults rather than stopping the app
        s.Path = path;
        return s;
    }

    public void Save()
    {
        if (Path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch { }
        Changed?.Invoke();
    }
}

public sealed class PomodoroSettings
{
    public bool Enabled { get; set; }
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int SessionsPerCycle { get; set; } = 4;
    public bool SoundEnabled { get; set; } = true;
    public string Sound { get; set; } = "hero_decorative-celebration-01";
}

public sealed class SoundSettings
{
    public bool ClaudeAlertEnabled { get; set; }
    public string ClaudeAlert { get; set; } = "alert_simple";
    public bool ClaudeDoneEnabled { get; set; }
    public string ClaudeDone { get; set; } = "notification_simple-02";
}
