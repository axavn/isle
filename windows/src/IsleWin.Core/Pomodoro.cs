namespace IsleWin.Core;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

/// Isle's PomodoroTimer (PomodoroTimer.swift) as pure logic: focus, short break, and a long break after the Nth
/// focus; start / pause / skip / reset. Anchored to a wall-clock end time (so sleeping counts as elapsed, as
/// his), the clock is injectable, and nothing is persisted. The host arms a one-shot for `EndDate` and calls
/// `Tick()` when it fires; `Tick()` does nothing if the interval hasn't really ended yet.
public sealed class PomodoroTimer
{
    readonly PomodoroSettings _settings;
    readonly Func<DateTimeOffset> _now;

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Focus;
    public bool IsRunning { get; private set; }
    /// Focus intervals finished in this cycle. It resets when the long break ENDS (or is skipped), as his code
    /// does, not when it begins as his comment says, so the dots stay full during the long break.
    public int CompletedInCycle { get; private set; }
    public int CompletedFocusTotal { get; private set; }
    public DateTimeOffset? EndDate { get; private set; }
    public TimeSpan RemainingWhenPaused { get; private set; }
    public TimeSpan PhaseDuration { get; private set; }

    /// Raised after the phase has rolled over (and a break has started), with the phase that ended.
    public event Action<PomodoroPhase>? IntervalEnded;

    public PomodoroTimer(PomodoroSettings settings, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        PhaseDuration = RemainingWhenPaused = DurationOf(PomodoroPhase.Focus);
    }

    public bool IsBreak => Phase != PomodoroPhase.Focus;
    public int SessionsPerCycle => Math.Max(1, _settings.SessionsPerCycle);

    /// False only in the pristine state a reset returns to; once touched it keeps its seat until reset.
    public bool IsActive => IsRunning || RemainingWhenPaused < PhaseDuration || CompletedInCycle > 0 || CompletedFocusTotal > 0;

    public TimeSpan Remaining() => EndDate is { } e ? Max(TimeSpan.Zero, e - _now()) : RemainingWhenPaused;

    /// 0 at the start of an interval, 1 when it is up.
    public double Progress()
    {
        if (PhaseDuration <= TimeSpan.Zero) return 0;
        return Math.Clamp(1 - Remaining().TotalSeconds / PhaseDuration.TotalSeconds, 0, 1);
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        EndDate = _now() + RemainingWhenPaused;
    }

    public void Pause()
    {
        if (!IsRunning) return;
        RemainingWhenPaused = Remaining();
        EndDate = null;
        IsRunning = false;
    }

    public void Toggle() { if (IsRunning) Pause(); else Start(); }

    /// Abandon the interval and land paused on the next one. Skipping a focus does not count it.
    public void Skip() => Advance(false);

    public void Reset()
    {
        IsRunning = false; EndDate = null;
        Phase = PomodoroPhase.Focus;
        CompletedInCycle = CompletedFocusTotal = 0;
        PhaseDuration = RemainingWhenPaused = DurationOf(PomodoroPhase.Focus);
    }

    /// Call when the end-time one-shot fires. Returns the phase that ended, or null if it was early / not running.
    public PomodoroPhase? Tick()
    {
        if (!IsRunning || Remaining() > TimeSpan.Zero) return null;
        var ended = Phase;
        Advance(true);
        if (ended == PomodoroPhase.Focus) Start(); // a break starts by itself; the next focus waits to be asked
        IntervalEnded?.Invoke(ended);
        return ended;
    }

    void Advance(bool completed)
    {
        IsRunning = false; EndDate = null;
        PomodoroPhase next;
        switch (Phase)
        {
            case PomodoroPhase.Focus:
                if (completed) { CompletedFocusTotal++; CompletedInCycle++; }
                next = CompletedInCycle >= SessionsPerCycle ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
                break;
            case PomodoroPhase.ShortBreak: next = PomodoroPhase.Focus; break;
            default: CompletedInCycle = 0; next = PomodoroPhase.Focus; break;
        }
        Phase = next;
        PhaseDuration = RemainingWhenPaused = DurationOf(next);
    }

    TimeSpan DurationOf(PomodoroPhase p) => TimeSpan.FromMinutes(Math.Max(1, p switch
    {
        PomodoroPhase.Focus => _settings.FocusMinutes,
        PomodoroPhase.ShortBreak => _settings.ShortBreakMinutes,
        _ => _settings.LongBreakMinutes,
    }));

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public static string Title(PomodoroPhase p) => p switch { PomodoroPhase.Focus => "Focus", PomodoroPhase.ShortBreak => "Short break", _ => "Long break" };

    /// `m:ss`, or `h:mm:ss` from an hour; whole seconds rounded UP so a fresh clock reads the full length.
    public static string Clock(TimeSpan t)
    {
        long s = (long)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// The widest the clock gets in an interval of this length, for a fixed-width slot.
    public static string Template(TimeSpan phaseDuration) =>
        phaseDuration.TotalSeconds >= 3600 ? "0:00:00" : phaseDuration.TotalSeconds >= 600 ? "00:00" : "0:00";
}
