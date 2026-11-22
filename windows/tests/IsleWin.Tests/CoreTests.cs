using System.Diagnostics;
using System.Text.Json.Nodes;
using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public sealed class Tmp : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "isle-test-" + Guid.NewGuid().ToString("N"));
    public IslePaths Paths;
    public Tmp() { Directory.CreateDirectory(Dir); Paths = new IslePaths(Dir); }
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class InstallerTests
{
    const string Exe = "C:/Users/x/.isle/bin/isle-cli.exe";
    const string Foreign = """{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo hi"}]}],"Custom":[{"hooks":[{"type":"command","command":"x"}]}]}}""";

    static int IsleCount(string json) => json.Split("isle-cli").Length - 1;

    [Fact]
    public void Merge_AddsEntriesAndKeepsForeign()
    {
        var m = JsonNode.Parse(HookInstaller.Merge(Foreign, Exe))!;
        Assert.Equal("opus", (string?)m["model"]);
        Assert.Equal("echo hi", (string?)m["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(2, m["hooks"]!["Stop"]!.AsArray().Count);
        Assert.NotNull(m["hooks"]!["Custom"]);
        var perm = m["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal("\"C:/Users/x/.isle/bin/isle-cli.exe\" ask", (string?)perm["command"]);
        Assert.Equal(330, (int)perm["timeout"]!);
        var pre = m["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal("AskUserQuestion", (string?)pre[1]!["matcher"]);
        Assert.Equal(60, (int)pre[1]!["hooks"]![0]!["timeout"]!);
        Assert.Equal(14, IsleCount(m.ToJsonString()));
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        var once = HookInstaller.Merge(Foreign, Exe);
        Assert.Equal(once, HookInstaller.Merge(once, Exe));
        Assert.Equal(once, HookInstaller.Merge(HookInstaller.Merge(Foreign, "C:/old/isle-cli.exe"), Exe)); // path change replaces
    }

    [Fact]
    public void Unmerge_RoundTrips()
    {
        var back = HookInstaller.Unmerge(HookInstaller.Merge(Foreign, Exe));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Foreign), JsonNode.Parse(back)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{}"), JsonNode.Parse(HookInstaller.Unmerge(HookInstaller.Merge(null, Exe)))));
    }

    [Fact]
    public void Merge_BadJsonStartsFresh()
    {
        Assert.Contains("isle-cli", HookInstaller.Merge("{not json", Exe));
    }

    [Fact]
    public void Install_CopiesFilesBacksUpOnce()
    {
        using var t = new Tmp();
        var src = Path.Combine(t.Dir, "src"); Directory.CreateDirectory(src);
        foreach (var f in new[] { "isle-cli.exe", "isle-cli.dll", "IsleWin.Core.dll", "other.dll" }) File.WriteAllText(Path.Combine(src, f), "x");
        Directory.CreateDirectory(Path.GetDirectoryName(t.Paths.ClaudeSettings)!);
        File.WriteAllText(t.Paths.ClaudeSettings, Foreign);
        HookInstaller.Install(t.Paths, src);
        HookInstaller.Install(t.Paths, src);
        Assert.True(File.Exists(Path.Combine(t.Paths.Bin, "isle-cli.exe")));
        Assert.True(File.Exists(Path.Combine(t.Paths.Bin, "IsleWin.Core.dll")));
        Assert.False(File.Exists(Path.Combine(t.Paths.Bin, "other.dll")));
        Assert.Equal(Foreign, File.ReadAllText(t.Paths.ClaudeSettings + ".isle-backup"));
        Assert.True(HookInstaller.IsInstalled(t.Paths));
        HookInstaller.Uninstall(t.Paths);
        Assert.False(HookInstaller.IsInstalled(t.Paths));
    }
}

public class StoreTests
{
    [Fact]
    public void Status_RoundTrips()
    {
        var s = new SessionStatus("abc", ClaudeState.NeedsApproval, "proj", @"C:\a\proj", "t.jsonl", "Bash", "ls", null, DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var json = StatusStore.Serialize(s);
        Assert.Contains("\"needs_approval\"", json);
        Assert.Contains("\"session_id\"", json);
        Assert.Equal(s, StatusStore.Parse(json));
    }

    [Fact]
    public void Readers_AreDefensive()
    {
        using var t = new Tmp();
        Assert.Empty(StatusStore.ReadAll(t.Paths));
        Assert.Empty(RegistryStore.ReadAll(t.Paths));
        Assert.Empty(RequestStore.ReadPending(t.Paths));
        Directory.CreateDirectory(t.Paths.Sessions);
        File.WriteAllText(Path.Combine(t.Paths.Sessions, "bad.json"), "{{{");
        File.WriteAllText(Path.Combine(t.Paths.Sessions, "ok.json"), """{"session_id":"s1","extra":1}""");
        var all = StatusStore.ReadAll(t.Paths);
        Assert.Single(all);
        Assert.Equal(ClaudeState.Idle, all[0].State);
        Assert.Null(StatusStore.Parse("nope"));
        Assert.Null(RegistryStore.Parse("[]"));
    }

    [Fact]
    public void Registry_ParsesAndChecksLiveness()
    {
        var me = Process.GetCurrentProcess();
        var r = RegistryStore.Parse($$"""{"pid":{{me.Id}},"sessionId":"s","procStart":"{{me.StartTime.ToFileTimeUtc()}}","status":"idle","updatedAt":5}""")!;
        Assert.Equal("s", r.SessionId);
        Assert.True(RegistryStore.IsAlive(r));
        Assert.False(RegistryStore.IsAlive(r with { ProcStart = (me.StartTime.ToFileTimeUtc() + 600_000_000).ToString() }));
        Assert.False(RegistryStore.IsAlive(r with { Pid = 0 }));
    }
}

public class RankerTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    static SessionStatus S(string id, ClaudeState st, int ageMin = 0) => new(id, st, id, null, null, null, null, null, Now.AddMinutes(-ageMin));
    static HashSet<string> Live(params string[] ids) => new(ids);

    [Fact]
    public void AttentionBeatsWorking()
    {
        var pick = SessionRanker.Select([S("a", ClaudeState.Working), S("b", ClaudeState.NeedsQuestion, 5)], Live("a", "b"), "a", null, Now);
        Assert.Equal("b", pick!.SessionId);
        Assert.Equal(0, SessionRanker.Rank(ClaudeState.NeedsApproval));
        Assert.True(SessionRanker.Rank(ClaudeState.Failed) < SessionRanker.Rank(ClaudeState.Working));
        Assert.True(SessionRanker.Rank(ClaudeState.Done) < SessionRanker.Rank(ClaudeState.Idle));
    }

    [Fact]
    public void KeepsCurrentWhileNotOutranked()
    {
        var list = new[] { S("a", ClaudeState.Working, 10), S("b", ClaudeState.Working, 0) };
        Assert.Equal("a", SessionRanker.Select(list, Live("a", "b"), "a", null, Now)!.SessionId);
        Assert.Equal("b", SessionRanker.Select(list, Live("a", "b"), null, null, Now)!.SessionId);
        Assert.Equal("b", SessionRanker.Select(list, Live("b"), "a", null, Now)!.SessionId); // current not live
    }

    [Fact]
    public void PinWins()
    {
        var list = new[] { S("a", ClaudeState.Idle), S("b", ClaudeState.NeedsApproval) };
        Assert.Equal("a", SessionRanker.Select(list, Live("a", "b"), "b", "a", Now)!.SessionId);
    }

    [Fact]
    public void FreshnessFallbackWithoutRegistry()
    {
        var list = new[] { S("old", ClaudeState.NeedsApproval, 31), S("new", ClaudeState.Idle, 29) };
        Assert.Equal("new", SessionRanker.Select(list, null, null, null, Now)!.SessionId);
        Assert.Null(SessionRanker.Select([S("old", ClaudeState.Idle, 31)], null, null, null, Now));
        Assert.Null(SessionRanker.Select(list, Live(), null, null, Now));
    }
}

public class ResponseTests
{
    [Fact]
    public void Allow_Deny_Shapes()
    {
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""", ResponseBuilder.Allow());
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"no way"}}}""", ResponseBuilder.Deny("no way"));
    }

    [Fact]
    public void Answers_KeepsInputAndAddsAnswers()
    {
        var input = JsonNode.Parse("""{"questions":[{"question":"Q?"}]}""")!.AsObject();
        var json = ResponseBuilder.Answers(input, new Dictionary<string, string> { ["Q?"] = "A, B" });
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"questions":[{"question":"Q?"}],"answers":{"Q?":"A, B"}}}}""", json);
        Assert.Null(input["answers"]); // input not mutated
    }
}

public class HookHandlerTests
{
    static string Stdin(string ev, string? tool = null, string extra = "") =>
        "{\"session_id\":\"s1\",\"hook_event_name\":\"" + ev + "\",\"cwd\":\"C:\\\\work\\\\my-proj\",\"transcript_path\":\"t.jsonl\""
        + (tool != null ? ",\"tool_name\":\"" + tool + "\",\"tool_input\":{\"file_path\":\"a.cs\"}" : "") + extra + "}";

    static SessionStatus Read(Tmp t) => StatusStore.ReadAll(t.Paths).Single();

    [Fact]
    public void SetState_WritesFile()
    {
        using var t = new Tmp();
        Assert.Equal("", HookHandler.Run("set-state", "working", Stdin("PreToolUse", "Edit"), t.Paths));
        var s = Read(t);
        Assert.Equal(ClaudeState.Working, s.State);
        Assert.Equal("my-proj", s.Project);
        Assert.Equal("Edit", s.Tool);
        Assert.Equal("a.cs", s.ToolTarget);
        HookHandler.Run("set-state", "working", Stdin("PostToolUse"), t.Paths);
        Assert.Null(Read(t).Tool);
        HookHandler.Run("set-state", "done", Stdin("Stop"), t.Paths);
        Assert.Equal(ClaudeState.Done, Read(t).State);
    }

    [Fact]
    public void AskUserQuestionPreToolUse_WritesNeedsQuestion()
    {
        using var t = new Tmp();
        HookHandler.Run("set-state", "working", Stdin("PreToolUse", "AskUserQuestion"), t.Paths);
        Assert.Equal(ClaudeState.NeedsQuestion, Read(t).State);
        HookHandler.Run("set-state", "working", Stdin("PostToolUse", "AskUserQuestion"), t.Paths);
        Assert.Equal(ClaudeState.Working, Read(t).State);
    }

    [Fact]
    public void NotifyFailEnd()
    {
        using var t = new Tmp();
        HookHandler.Run("set-state", "working", Stdin("UserPromptSubmit"), t.Paths);
        HookHandler.Run("notify", null, Stdin("Notification", extra: ",\"notification_type\":\"other\""), t.Paths);
        Assert.Equal(ClaudeState.Working, Read(t).State);
        HookHandler.Run("notify", null, Stdin("Notification", extra: ",\"notification_type\":\"permission_prompt\""), t.Paths);
        Assert.Equal(ClaudeState.NeedsApproval, Read(t).State);
        HookHandler.Run("notify", null, Stdin("Notification", extra: ",\"notification_type\":\"idle_prompt\""), t.Paths);
        Assert.Equal(ClaudeState.WaitingInput, Read(t).State);
        HookHandler.Run("fail", null, Stdin("StopFailure", extra: ",\"error\":\"rate_limit\""), t.Paths);
        Assert.Equal("rate_limit", Read(t).ErrorType);
        Assert.Equal(ClaudeState.Failed, Read(t).State);
        HookHandler.Run("end", null, Stdin("SessionEnd"), t.Paths);
        Assert.Empty(StatusStore.ReadAll(t.Paths));
    }

    [Fact]
    public void BadStdin_NeverThrows()
    {
        using var t = new Tmp();
        Assert.Equal("", HookHandler.Run("set-state", "idle", "garbage", t.Paths));
        Assert.Equal("", HookHandler.Run("ask", null, "", t.Paths));
        Assert.Equal("", HookHandler.Run("bogus", null, Stdin("X"), t.Paths));
    }

    [Fact]
    public void Ask_NoMarker_ReturnsQuicklyAndSetsState()
    {
        using var t = new Tmp();
        var sw = Stopwatch.StartNew();
        var r = HookHandler.Run("ask", null, Stdin("PermissionRequest", "Bash"), t.Paths);
        Assert.Equal("", r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(ClaudeState.NeedsApproval, Read(t).State);
        Assert.False(Directory.Exists(t.Paths.Requests) && Directory.GetFiles(t.Paths.Requests).Length > 0);
    }

    [Fact]
    public void Ask_ActiveMarker_ReturnsResponseFromApp()
    {
        using var t = new Tmp();
        AnsweringMarker.Write(t.Paths, Environment.ProcessId);
        Assert.True(AnsweringMarker.IsActive(t.Paths));
        var app = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var p = RequestStore.ReadPending(t.Paths).FirstOrDefault();
                if (p != null)
                {
                    Assert.Equal(RequestKind.Permission, p.Kind);
                    Assert.Equal("Bash", p.ToolName);
                    RequestStore.WriteResponse(t.Paths, p.RequestId, ResponseBuilder.Allow());
                    return;
                }
                Thread.Sleep(50);
            }
        });
        var r = HookHandler.Run("ask", null, Stdin("PermissionRequest", "Bash"), t.Paths);
        app.Wait();
        Assert.Equal(ResponseBuilder.Allow(), r);
        Assert.Empty(Directory.GetFiles(t.Paths.Requests));
        AnsweringMarker.Remove(t.Paths);
        Assert.False(AnsweringMarker.IsActive(t.Paths));
    }

    [Fact]
    public void AskQuestion_ParsesQuestions()
    {
        using var t = new Tmp();
        AnsweringMarker.Write(t.Paths, Environment.ProcessId);
        var stdin = """{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Pick?","header":"H","multiSelect":true,"options":[{"label":"A","description":"d"},{"label":"B"}]}]}}""";
        PendingRequest? seen = null; string? answer = null;
        var app = Task.Run(() =>
        {
            for (var i = 0; i < 100 && seen == null; i++)
            {
                seen = RequestStore.ReadPending(t.Paths).FirstOrDefault();
                Thread.Sleep(50);
            }
            var input = RequestStore.ReadToolInput(seen!)!;
            answer = ResponseBuilder.Answers(input, new Dictionary<string, string> { ["Pick?"] = "A" });
            RequestStore.WriteResponse(t.Paths, seen!.RequestId, answer);
        });
        var r = HookHandler.Run("ask-question", null, stdin, t.Paths);
        app.Wait();
        Assert.Equal(answer, r);
        Assert.Equal(RequestKind.Question, seen!.Kind);
        var q = Assert.Single(seen.Questions);
        Assert.True(q.MultiSelect);
        Assert.Equal(["A", "B"], q.Options.Select(o => o.Label));
        Assert.Equal("d", q.Options[0].Description);
    }
}

public class TranscriptTests
{
    static string Line(string type, string content) => "{\"type\":\"" + type + "\",\"message\":{\"role\":\"" + type + "\",\"content\":" + content + "}}";

    [Fact]
    public void LastAssistantText_And_UserText()
    {
        using var t = new Tmp();
        var f = Path.Combine(t.Dir, "t.jsonl");
        File.WriteAllLines(f,
        [
            Line("user", "\"hello there\""),
            Line("assistant", """[{"type":"text","text":"first"}]"""),
            Line("user", """[{"type":"text","text":"second prompt"}]"""),
            Line("assistant", """[{"type":"text","text":"final"},{"type":"text","text":"answer"},{"type":"tool_use","id":"x"}]"""),
            Line("assistant", """[{"type":"tool_use","id":"y"}]"""),
            "not json",
            """{"type":"summary"}""",
        ]);
        Assert.Equal("final\nanswer", TranscriptReader.LastAssistantText(f));
        Assert.True(TranscriptReader.ContainsUserText(f, "second prompt"));
        Assert.True(TranscriptReader.ContainsUserText(f, "hello"));
        Assert.False(TranscriptReader.ContainsUserText(f, "final"));
        Assert.Null(TranscriptReader.LastAssistantText(Path.Combine(t.Dir, "missing.jsonl")));
    }

    [Fact]
    public void ReadsOnlyTail()
    {
        using var t = new Tmp();
        var f = Path.Combine(t.Dir, "t.jsonl");
        var big = Line("assistant", $$"""[{"type":"text","text":"{{new string('x', 5000)}}"}]""");
        File.WriteAllLines(f, [Line("user", "\"early marker\""), big, Line("assistant", """[{"type":"text","text":"tail"}]""")]);
        Assert.Equal("tail", TranscriptReader.LastAssistantText(f, 200));
        Assert.False(TranscriptReader.ContainsUserText(f, "early marker", 200));
        Assert.True(TranscriptReader.ContainsUserText(f, "early marker"));
    }
}
