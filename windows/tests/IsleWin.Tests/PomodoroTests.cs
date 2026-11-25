using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public class PomodoroTests
{
    DateTimeOffset _t = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    PomodoroTimer Make(int sessions = 4) => new(new PomodoroSettings { SessionsPerCycle = sessions }, () => _t);
    void Elapse(PomodoroTimer p) { _t = p.EndDate!.Value; p.Tick(); }

    [Fact] public void StartsPristineAndPaused()
    {
        var p = Make();
        Assert.False(p.IsActive); Assert.False(p.IsRunning);
        Assert.Equal(PomodoroPhase.Focus, p.Phase);
        Assert.Equal("25:00", PomodoroTimer.Clock(p.Remaining()));
    }

    [Fact] public void FocusEndsIntoRunningShortBreak()
    {
        var p = Make(); PomodoroPhase? ended = null; p.IntervalEnded += e => ended = e;
        p.Start(); Elapse(p);
        Assert.Equal(PomodoroPhase.Focus, ended);
        Assert.Equal(PomodoroPhase.ShortBreak, p.Phase); Assert.True(p.IsRunning);
        Assert.Equal(1, p.CompletedInCycle);
    }

    [Fact] public void BreakEndsIntoPausedFocus()
    {
        var p = Make(); p.Start(); Elapse(p); Elapse(p);
        Assert.Equal(PomodoroPhase.Focus, p.Phase); Assert.False(p.IsRunning);
        Assert.True(p.IsActive);
    }

    [Fact] public void LongBreakAfterNthFocusAndCountResetsWhenItEnds()
    {
        var p = Make(2);
        p.Start(); Elapse(p); Elapse(p);       // focus 1, short break
        p.Start(); Elapse(p);                  // focus 2 -> long break
        Assert.Equal(PomodoroPhase.LongBreak, p.Phase);
        Assert.Equal(2, p.CompletedInCycle);   // still full during the long break
        Elapse(p);
        Assert.Equal(PomodoroPhase.Focus, p.Phase);
        Assert.Equal(0, p.CompletedInCycle); Assert.Equal(2, p.CompletedFocusTotal);
    }

    [Fact] public void SkipFocusDoesNotCount()
    {
        var p = Make(); p.Skip();
        Assert.Equal(PomodoroPhase.ShortBreak, p.Phase); Assert.False(p.IsRunning);
        Assert.Equal(0, p.CompletedFocusTotal); Assert.Equal(0, p.CompletedInCycle);
    }

    [Fact] public void SkipLongBreakClearsCycle()
    {
        var p = Make(1); p.Start(); Elapse(p);
        Assert.Equal(PomodoroPhase.LongBreak, p.Phase);
        p.Skip();
        Assert.Equal(0, p.CompletedInCycle); Assert.Equal(PomodoroPhase.Focus, p.Phase); Assert.False(p.IsRunning);
    }

    [Fact] public void ResetReturnsToPristine()
    {
        var p = Make(); p.Start(); Elapse(p); p.Reset();
        Assert.False(p.IsActive); Assert.Equal(PomodoroPhase.Focus, p.Phase); Assert.Equal(0, p.CompletedFocusTotal);
    }

    [Fact] public void PauseKeepsRemainingAndProgress()
    {
        var p = Make(); p.Start(); _t += TimeSpan.FromMinutes(5); p.Pause();
        Assert.Equal(TimeSpan.FromMinutes(20), p.RemainingWhenPaused);
        Assert.Equal(0.2, p.Progress(), 6); Assert.True(p.IsActive);
        _t += TimeSpan.FromHours(1); Assert.Equal(TimeSpan.FromMinutes(20), p.Remaining());
    }

    [Fact] public void EarlyTickDoesNothing()
    {
        var p = Make(); p.Start(); _t += TimeSpan.FromMinutes(24);
        Assert.Null(p.Tick()); Assert.Equal(PomodoroPhase.Focus, p.Phase);
    }

    [Fact] public void SleepPastEndRollsOnePhase()
    {
        var p = Make(); p.Start(); _t += TimeSpan.FromHours(3); p.Tick();
        Assert.Equal(PomodoroPhase.ShortBreak, p.Phase); Assert.Equal(1, p.CompletedFocusTotal);
    }

    [Fact] public void ClockRoundsUpAndSwitchesToHours()
    {
        Assert.Equal("0:01", PomodoroTimer.Clock(TimeSpan.FromSeconds(0.2)));
        Assert.Equal("4:09", PomodoroTimer.Clock(TimeSpan.FromSeconds(248.5)));
        Assert.Equal("0:00", PomodoroTimer.Clock(TimeSpan.Zero));
        Assert.Equal("1:00:00", PomodoroTimer.Clock(TimeSpan.FromHours(1)));
        Assert.Equal("1:05:09", PomodoroTimer.Clock(TimeSpan.FromSeconds(3909)));
    }

    [Fact] public void Templates()
    {
        Assert.Equal("0:00", PomodoroTimer.Template(TimeSpan.FromMinutes(5)));
        Assert.Equal("00:00", PomodoroTimer.Template(TimeSpan.FromMinutes(25)));
        Assert.Equal("0:00:00", PomodoroTimer.Template(TimeSpan.FromMinutes(60)));
    }
}
