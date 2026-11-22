using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public class ClaudeTextTests
{
    [Fact]
    public void Limit_FromClaudeCodesWording()
    {
        var l = ClaudeText.ParseLimit("You've hit your 5-hour limit · resets 9:30pm (America/Toronto)")!;
        Assert.Equal("5-hour limit", l.Title);
        Assert.Equal("Resets 9:30pm", l.Detail);
        var l2 = ClaudeText.ParseLimit("You've hit your weekly limit · resets 7:40pm (America/Toronto) · out of usage credits")!;
        Assert.Equal("Weekly limit", l2.Title);
        Assert.Equal("Resets 7:40pm", l2.Detail);
        Assert.Null(ClaudeText.ParseLimit("All done — the build passes."));
        Assert.Equal("7:40pm", ClaudeText.ParseContinue("Usage limit reached · set to continue automatically at 7:40pm"));
        Assert.Equal("Resets 9:30pm · continues automatically at 9:30pm", (l with { ContinuesAt = "9:30pm" }).Detail);
    }

    [Fact]
    public void Limit_MissingPartsAreLeftOut()
    {
        var l = ClaudeText.ParseLimit("You've hit your usage limit")!;
        Assert.Equal("Usage limit", l.Title);
        Assert.Equal("", l.Detail);
    }

    [Fact]
    public void SubagentLine_CountsTypes()
    {
        Assert.Equal("", ClaudeText.SubagentLine(null));
        Assert.Equal("1 agent · Explore", ClaudeText.SubagentLine(new[] { new Subagent("a", "Explore") }));
        Assert.Equal("3 agents · Explore ×2, Plan", ClaudeText.SubagentLine(new[] { new Subagent("a", "Explore"), new Subagent("b", "Plan"), new Subagent("c", "Explore") }));
    }

    [Theory]
    [InlineData("**Fixed** the [hover bug](x.cs) in `Track()`.\n\nMore detail here.", "Fixed the hover bug in Track().")]
    [InlineData("## Summary\n\nThe pill now shrinks.", "The pill now shrinks.")]
    [InlineData("## Summary", "Summary")]
    [InlineData("Changes:\n\n- added the gap\n- moved the crumb", "Changes: added the gap")]
    [InlineData("```cs\nvar x = 1;\n```\nBuilt fine.", "Built fine.")]
    public void DoneLine_FirstProseWithoutMarkdown(string md, string expected) => Assert.Equal(expected, ClaudeText.DoneLine(md));

    [Fact]
    public void DoneLine_CutsAtSentenceEnd()
    {
        var s = "The island now slides between Home, Music and Claude with a two-finger swipe. It also detaches when you press and hold it, and it docks again when dropped near the top of the screen.";
        Assert.Equal("The island now slides between Home, Music and Claude with a two-finger swipe.", ClaudeText.DoneLine(s));
    }
}

public class SubagentHookTests
{
    static string In(string ev, string extra = "") => "{\"session_id\":\"s1\",\"hook_event_name\":\"" + ev + "\",\"cwd\":\"C:/w/p\"" + extra + "}";

    [Fact]
    public void StartStop_TracksAgents_AndKeepsState()
    {
        using var t = new Tmp();
        HookHandler.Run("set-state", "working", In("UserPromptSubmit"), t.Paths);
        HookHandler.Run("subagent-start", null, In("SubagentStart", ",\"agent_id\":\"a1\",\"agent_type\":\"Explore\""), t.Paths);
        HookHandler.Run("subagent-start", null, In("SubagentStart", ",\"agent_id\":\"a2\",\"agent_type\":\"Plan\""), t.Paths);
        HookHandler.Run("set-state", "working", In("PostToolUse"), t.Paths);
        var s = StatusStore.ReadAll(t.Paths).Single();
        Assert.Equal(ClaudeState.Working, s.State);
        Assert.Equal(new[] { "Explore", "Plan" }, s.Subagents!.Select(a => a.Type));
        HookHandler.Run("subagent-stop", null, In("SubagentStop", ",\"agent_id\":\"a1\",\"agent_type\":\"Explore\""), t.Paths);
        HookHandler.Run("set-state", "done", In("Stop", ",\"last_assistant_message\":\"All set.\""), t.Paths);
        s = StatusStore.ReadAll(t.Paths).Single();
        Assert.Equal(ClaudeState.Done, s.State);
        Assert.Equal("Plan", s.Subagents!.Single().Type);
        Assert.Equal("All set.", s.LastMessage);
        HookHandler.Run("set-state", "working", In("UserPromptSubmit"), t.Paths);
        Assert.Null(StatusStore.ReadAll(t.Paths).Single().LastMessage);
    }
}
