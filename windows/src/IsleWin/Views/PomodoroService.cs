using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using IsleWin.Core;
using IsleWin.Media;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;

namespace IsleWin;

/// The Pomodoro for the UI thread: wraps the pure `PomodoroTimer`, arms a one-shot for the phase end (re-armed if
/// it fires early, and checked again on resume from sleep), ticks once a second while running so the clock
/// redraws, and plays the interval-end sound. Create on the UI thread. Settings are read live from
/// `IsleSettings.Current.Pomodoro` (new lengths apply from the next interval, as his).
/// Every property change raises PropertyChanged with an empty name (everything changed).
public sealed class PomodoroService : INotifyPropertyChanged, IDisposable
{
    readonly PomodoroTimer _t;
    readonly DispatcherTimer _end = new(), _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    Color _accent = Color.FromRgb(153, 153, 153), _secondary = Color.FromRgb(153, 153, 153);

    public event PropertyChangedEventHandler? PropertyChanged;

    public PomodoroService() : this(new PomodoroTimer(IsleSettings.Current.Pomodoro)) { }

    public PomodoroService(PomodoroTimer timer)
    {
        _t = timer;
        _end.Tick += (_, _) => { _end.Stop(); Check(); };
        _tick.Tick += (_, _) => Changed();
        _t.IntervalEnded += _ =>
        {
            var s = IsleSettings.Current.Pomodoro;
            if (s.SoundEnabled) Sounds.Play(s.Sound);
        };
        SystemEvents.PowerModeChanged += OnPower;
    }

    public void Dispose() { SystemEvents.PowerModeChanged -= OnPower; _end.Stop(); _tick.Stop(); }

    void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) System.Windows.Application.Current?.Dispatcher.BeginInvoke(Check);
    }

    void Check() { _t.Tick(); Sync(); }

    /// The album palette's accent (focus) and secondary (break); the grey default when no music colours exist.
    public void SetPalette(Color accent, Color secondary) { _accent = accent; _secondary = secondary; Changed(); }

    void Sync()
    {
        if (_t.IsRunning && _t.EndDate is { } e)
        {
            var wait = e - DateTimeOffset.UtcNow;
            _end.Interval = wait > TimeSpan.Zero ? wait + TimeSpan.FromMilliseconds(30) : TimeSpan.FromMilliseconds(1);
            _end.Stop(); _end.Start(); _tick.Start();
        }
        else { _end.Stop(); _tick.Stop(); }
        Changed();
    }

    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public PomodoroPhase Phase => _t.Phase;
    public bool IsRunning => _t.IsRunning;
    public bool IsActive => _t.IsActive;
    /// On the collapsed pill from the first start until reset (running, paused or a finished cycle). The
    /// caller also requires `IsleSettings.Current.Pomodoro.Enabled`.
    public bool IsShownOnPill => _t.IsActive;
    public string ClockText => PomodoroTimer.Clock(_t.Remaining());
    /// The clock's widest text for this interval, to size a fixed slot (`0:00`, `00:00`, `0:00:00`).
    public string ClockTemplate => PomodoroTimer.Template(_t.PhaseDuration);
    /// True from an hour left: the face uses its smaller clock (18 pt, else 23).
    public bool IsLongClock => _t.Remaining().TotalSeconds >= 3600;
    public double Progress => _t.Progress();
    public int CycleIndex => _t.CompletedInCycle;
    public int SessionsPerCycle => _t.SessionsPerCycle;
    public Color TintColor => _t.IsBreak ? _secondary : _accent;
    public Brush Tint { get { var b = new SolidColorBrush(TintColor); b.Freeze(); return b; } }
    public string Title => PomodoroTimer.Title(_t.Phase);

    public string Detail =>
        _t.IsRunning ? (_t.IsBreak ? "Step away — the next focus waits for you" : "Heads down")
        : _t.IsActive ? "Paused"
        : _t.IsBreak ? "Ready when you are" : "Press play to start a focus session";

    /// The Home tile's text: "clock\nline 2", empty until the timer has been touched.
    /// Focus: "Focus · 2 of 4" (running) or "Paused · 2 of 4"; break: "Short break" or "Paused · Short break".
    public string Glance
    {
        get
        {
            if (!_t.IsActive) return "";
            string line2 = !_t.IsBreak
                ? $"{(_t.IsRunning ? Title : "Paused")} · {Math.Min(_t.CompletedInCycle + 1, _t.SessionsPerCycle)} of {_t.SessionsPerCycle}"
                : _t.IsRunning ? Title : "Paused · " + Title;
            return ClockText + "\n" + line2;
        }
    }

    public void Toggle() { _t.Toggle(); Sync(); }
    public void Skip() { _t.Skip(); Sync(); }
    public void Reset() { _t.Reset(); Sync(); }
}
