using System.Text.Json.Nodes;
using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public class QuestionTests
{
    const string Two = """{"session_id":"s1","hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"One?","header":"H1","options":[{"label":"A","description":"da"},{"label":"B"}]},{"question":"Two?","multiSelect":true,"options":[{"label":"X"},{"label":"Y"},{"label":"Z"}]}]}}""";

    static SessionStatus Read(Tmp t) => StatusStore.ReadAll(t.Paths).Single();

    [Fact]
    public void PermissionRequestForAskUserQuestion_IsNotAnApproval()
    {
        using var t = new Tmp();
        AnsweringMarker.Write(t.Paths, Environment.ProcessId); // the app is up, yet it must not block or ask
        var r = HookHandler.Run("ask", null, Two, t.Paths);
        Assert.Equal("", r);
        Assert.Equal(ClaudeState.NeedsQuestion, Read(t).State);
        Assert.False(Directory.Exists(t.Paths.Requests) && Directory.GetFiles(t.Paths.Requests).Length > 0);
    }

    [Fact]
    public void LateNotification_DoesNotDowngradeQuestion()
    {
        using var t = new Tmp();
        HookHandler.Run("ask", null, Two, t.Paths);
        HookHandler.Run("notify", null, """{"session_id":"s1","notification_type":"permission_prompt"}""", t.Paths);
        Assert.Equal(ClaudeState.NeedsQuestion, Read(t).State);
    }

    [Fact]
    public void AskQuestion_WithoutOptions_DoesNotBlock()
    {
        using var t = new Tmp();
        AnsweringMarker.Write(t.Paths, Environment.ProcessId);
        var stdin = """{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Free?","options":[]}]}}""";
        Assert.Equal("", HookHandler.Run("ask-question", null, stdin, t.Paths));
        Assert.Equal(ClaudeState.NeedsQuestion, Read(t).State);
    }

    [Fact]
    public void StrayPermissionRequestFileForAskUserQuestion_IsIgnored()
    {
        using var t = new Tmp();
        Directory.CreateDirectory(t.Paths.Requests);
        File.WriteAllText(Path.Combine(t.Paths.Requests, "r1.json"),
            """{"request_id":"r1","kind":"permission","session_id":"s1","payload":{"tool_name":"AskUserQuestion","tool_input":{"questions":[]}}}""");
        Assert.Empty(RequestStore.ReadPending(t.Paths));
    }

    [Fact]
    public void ReadPending_ParsesSeveralQuestions()
    {
        using var t = new Tmp();
        Directory.CreateDirectory(t.Paths.Requests);
        var payload = JsonNode.Parse(Two)!;
        File.WriteAllText(Path.Combine(t.Paths.Requests, "q1.json"),
            new JsonObject { ["request_id"] = "q1", ["kind"] = "question", ["session_id"] = "s1", ["payload"] = payload }.ToJsonString());
        var p = Assert.Single(RequestStore.ReadPending(t.Paths));
        Assert.Equal(RequestKind.Question, p.Kind);
        Assert.Equal(2, p.Questions.Count);
        Assert.Equal("H1", p.Questions[0].Header);
        Assert.False(p.Questions[0].MultiSelect);
        Assert.True(p.Questions[1].MultiSelect);
        Assert.Equal(["X", "Y", "Z"], p.Questions[1].Options.Select(o => o.Label));
    }

    [Fact]
    public void JoinPicks_KeepsOfferedOrder_AndAnswersCarryBothQuestions()
    {
        var q = new Question("Two?", null, [new("X", null), new("Y", null), new("Z", null)], true);
        Assert.Equal("X, Z", ResponseBuilder.JoinPicks(q, new HashSet<string> { "Z", "X" }));
        var json = JsonNode.Parse(ResponseBuilder.Answers(JsonNode.Parse(Two)!["tool_input"]!.AsObject(),
            new Dictionary<string, string> { ["One?"] = "A", ["Two?"] = "X, Z" }))!;
        var a = json["hookSpecificOutput"]!["updatedInput"]!["answers"]!;
        Assert.Equal("A", (string?)a["One?"]);
        Assert.Equal("X, Z", (string?)a["Two?"]);
        Assert.Equal(2, json["hookSpecificOutput"]!["updatedInput"]!["questions"]!.AsArray().Count);
        Assert.Equal("{}", ResponseBuilder.Terminal());
    }
}

public class AtomicFileTests
{
    [Fact]
    public async Task Write_RetriesWhileAnotherProcessHoldsTheFileOpen()
    {
        using var t = new Tmp();
        var path = Path.Combine(t.Dir, "s.json");
        File.WriteAllText(path, "old");
        // open without FILE_SHARE_DELETE: replacing the file fails until it is released
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(() => { Thread.Sleep(150); held.Dispose(); });
        AtomicFile.WriteAllText(path, "new");
        await release;
        Assert.Equal("new", AtomicFile.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(t.Dir, "*.tmp-*"));
    }

    [Fact]
    public void Read_WorksWhileAnotherHandleIsOpenForWrite()
    {
        using var t = new Tmp();
        var path = Path.Combine(t.Dir, "s.json");
        File.WriteAllText(path, "text");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal("text", AtomicFile.ReadAllText(path));
    }
}
