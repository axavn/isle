using System.Text.Json.Nodes;
using IsleWin.Claude;
using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

sealed class FakeHost : IIslandHost
{
    public List<IslandToast> Toasts = new();
    public void RequestKeyboard(bool wantsKeyboard) { }
    public void RequestOpen(bool open) { }
    public void ShowFace(IslandFace face) { }
    public void Toast(IslandToast toast) => Toasts.Add(toast);
}

public class ApprovalTextTests
{
    const string Home = @"C:\Users\me";

    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ToolLabel_McpAndPlan()
    {
        Assert.Equal("acme › deploy_service", ClaudeText.ToolLabel("mcp__acme__deploy_service"));
        Assert.Equal("Acme Inc › deploy_service", ClaudeText.ToolLabel("mcp__acme__deploy_service", "Acme Inc"));
        Assert.Equal("Plan", ClaudeText.ToolLabel("ExitPlanMode"));
        Assert.Equal("Bash", ClaudeText.ToolLabel("Bash"));
    }

    [Fact]
    public void Notes_CountLines()
    {
        Assert.Equal("−1 +1 lines", ClaudeText.Note("Edit", O("""{"old_string":"a","new_string":"b\n"}""")));
        Assert.Equal("−2 +0 lines · every occurrence", ClaudeText.Note("Edit", O("""{"old_string":"a\nb","new_string":"","replace_all":true}""")));
        Assert.Equal("2 edits", ClaudeText.Note("MultiEdit", O("""{"edits":[{},{}]}""")));
        Assert.Equal("1 line", ClaudeText.Note("Write",O("""{"content":"x"}""")));
        Assert.Equal("empty file", ClaudeText.Note("Write", O("""{"content":""}""")));
        Assert.Equal("3 lines", ClaudeText.Note("Write", O("""{"content":"a\nb\nc\n"}""")));
        Assert.Equal("replace · cell c1", ClaudeText.Note("NotebookEdit", O("""{"cell_id":"c1"}""")));
        Assert.Equal("insert", ClaudeText.Note("NotebookEdit", O("""{"edit_mode":"insert"}""")));
        Assert.Null(ClaudeText.Note("Bash", O("""{"command":"ls"}""")));
    }

    [Fact]
    public void Subject_PicksPathCommandAndArguments()
    {
        Assert.Equal(@"src\a.cs", ClaudeText.Subject("Edit", O("""{"file_path":"C:\\proj\\src\\a.cs"}"""), @"C:\proj", Home));
        Assert.Equal(@"~\x.txt", ClaudeText.Subject("Read", O("""{"file_path":"C:\\Users\\me\\x.txt"}"""), @"C:\proj", Home));
        Assert.Equal("echo a ↵ echo b", ClaudeText.Subject("Bash", O("""{"command":"echo a\necho b","description":"d"}"""), null, Home));
        Assert.Equal("""a: "x/y", b: 2""", ClaudeText.Subject("mcp__s__t", O("""{"b":2,"a":"x/y"}"""), null, Home));
        Assert.Equal("a: 1", ClaudeText.Subject("Weird", O("""{"a":1}"""), null, Home));
        Assert.Null(ClaudeText.Subject("Weird", O("{}"), null, Home));
    }

    [Fact]
    public void Plan_OutlineStripsHeadings()
    {
        var p = ClaudeText.Approval(JsonNode.Parse("""{"tool_name":"ExitPlanMode","tool_input":{"plan":"# Title\n\n## Step one\n- do it\n"}}"""), Home);
        Assert.True(p.IsPlan);
        Assert.Equal("Title ↵ Step one ↵ - do it", p.Subject);
        Assert.Null(p.Note);
        Assert.Null(p.Remember);
    }

    [Fact]
    public void Remember_DecodesAndLabels()
    {
        var one = ClaudeText.Remember(JsonNode.Parse("""[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm run *"}],"behavior":"allow","destination":"localSettings"}]"""), null, Home)!;
        Assert.Equal(new[] { "Allow Bash(npm run *)" }, one.Changes);
        Assert.Equal("this project · .claude/settings.local.json", one.Scope);

        var two = ClaudeText.Remember(JsonNode.Parse("""[{"type":"addRules","rules":[{"toolName":"WebSearch"},{"toolName":"Read","ruleContent":"//etc/**"}],"behavior":"allow","destination":"session"},{"type":"setMode","mode":"acceptEdits","destination":"userSettings"},{"type":"addDirectories","directories":["C:\\Users\\me\\docs"],"destination":"session"}]"""), null, Home)!;
        Assert.Equal(new[] { "Allow WebSearch, Read(//etc/**)", "Switch to acceptEdits mode", @"Allow access to ~\docs" }, two.Changes);
        Assert.Equal("this session; every project · ~/.claude/settings.json", two.Scope);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"deny","destination":"session"}]""")]
    [InlineData("""[{"type":"removeRules","destination":"session"}]""")]
    [InlineData("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"weird"}]""")]
    [InlineData("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"},{"type":"nope","destination":"session"}]""")]
    public void Remember_AllOrNothing(string json) => Assert.Null(ClaudeText.Remember(JsonNode.Parse(json), null, Home));

    [Fact]
    public void Responses_PlanAllowEchoesInput_DenyWording()
    {
        var input = O("""{"plan":"# P\nx","planFilePath":"C:\\p\\plan.md"}""");
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedInput":{"plan":"# P\nx","planFilePath":"C:\\p\\plan.md"}}}}""", ResponseBuilder.AllowPlan(input));
        Assert.Equal("The user denied this tool call from the Isle notch.", ResponseBuilder.DenyMessage(false));
        Assert.Equal("The user rejected this plan from the Isle notch.", ResponseBuilder.DenyMessage(true));
        Assert.Equal("The user denied this tool call from the Isle notch and said: use rg", ResponseBuilder.DenyMessage(false, "  use rg "));
        Assert.Equal("The user rejected this plan from the Isle notch and said: smaller steps", ResponseBuilder.DenyMessage(true, "smaller steps"));
        Assert.Equal("The user denied this tool call from the Isle notch.", ResponseBuilder.DenyMessage(false, "   "));
    }
}

public class KeyDecisionTests : IDisposable
{
    readonly Tmp _t = new();
    readonly FakeHost _host = new();
    ClaudeViewModel? _vm;

    public void Dispose() { _vm?.Stop(); _t.Dispose(); }

    ClaudeViewModel Setup(string kind, string payloadJson, ClaudeState state)
    {
        Directory.CreateDirectory(_t.Paths.Sessions); Directory.CreateDirectory(_t.Paths.Requests);
        File.WriteAllText(Path.Combine(_t.Paths.Sessions, "s1.json"),
            StatusStore.Serialize(new SessionStatus("s1", state, "proj", @"C:\proj", null, null, null, null, DateTimeOffset.UtcNow)));
        File.WriteAllText(Path.Combine(_t.Paths.Requests, "r1.json"),
            new JsonObject { ["request_id"] = "r1", ["kind"] = kind, ["session_id"] = "s1", ["created_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"), ["payload"] = JsonNode.Parse(payloadJson) }.ToJsonString());
        _vm = new ClaudeViewModel(_host, _t.Paths);
        _vm.Start();
        return _vm;
    }

    JsonNode? Response() => File.Exists(Path.Combine(_t.Paths.Requests, "r1.response.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(_t.Paths.Requests, "r1.response.json"))) : null;

    const string Multi = """{"session_id":"s1","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Pick?","multiSelect":true,"options":[{"label":"X"},{"label":"Y"},{"label":"Z"}]}]}}""";
    const string Single = """{"session_id":"s1","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Pick?","options":[{"label":"X"},{"label":"Y"}]}]}}""";
    const string Sugg = """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm run *"}],"behavior":"allow","destination":"localSettings"},{"type":"setMode","mode":"acceptEdits","destination":"session"}]""";

    [Fact]
    public void KeyOption_MultiSelectToggles_ApproveSubmits()
    {
        var vm = Setup("question", Multi, ClaudeState.NeedsQuestion);
        var k = vm.Keys!;
        Assert.True(k.IsQuestion); Assert.True(k.MultiSelect); Assert.Equal(3, k.Options);
        Assert.False(vm.KeyApprove());              // nothing picked: Done does nothing
        Assert.True(vm.KeyOption(1)); Assert.True(vm.KeyOption(3)); Assert.True(vm.KeyOption(3)); Assert.True(vm.KeyOption(2));
        Assert.Null(Response()); Assert.Empty(_host.Toasts);
        Assert.False(vm.KeyOption(4)); Assert.False(vm.KeyOption(0));
        Assert.True(vm.KeyApprove());
        Assert.Equal("X, Y", (string?)Response()!["hookSpecificOutput"]!["updatedInput"]!["answers"]!["Pick?"]);
        var t = Assert.Single(_host.Toasts);
        Assert.Equal(("shortcut", "proj · answered", (string?)null), (t.Kind, t.Text, t.Tint));
        Assert.Null(vm.Keys);
    }

    [Fact]
    public void KeyOption_SingleSelectAnswersAtOnce()
    {
        var vm = Setup("question", Single, ClaudeState.NeedsQuestion);
        Assert.False(vm.KeyApprove());
        Assert.True(vm.KeyOption(2));
        Assert.Equal("Y", (string?)Response()!["hookSpecificOutput"]!["updatedInput"]!["answers"]!["Pick?"]);
        Assert.Equal("proj · answered", Assert.Single(_host.Toasts).Text);
    }

    [Fact]
    public void KeyApprove_PlanSendsUpdatedInput_AndToasts()
    {
        var vm = Setup("permission", """{"session_id":"s1","tool_name":"ExitPlanMode","cwd":"C:\\proj","tool_input":{"plan":"# P","planFilePath":"p.md"}}""", ClaudeState.NeedsApproval);
        Assert.Equal("Plan ready", vm.Headline);
        Assert.False(vm.Keys!.IsQuestion); Assert.False(vm.Keys.HasAlways);
        Assert.True(vm.KeyApprove());
        var d = Response()!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", (string?)d["behavior"]);
        Assert.Equal("p.md", (string?)d["updatedInput"]!["planFilePath"]);
        var t = Assert.Single(_host.Toasts);
        Assert.Equal(("shortcut", "proj · approved", "#34C759"), (t.Kind, t.Text, t.Tint));
    }

    [Fact]
    public void KeyDeny_DeniesWithAttribution()
    {
        var vm = Setup("permission", """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"rm x"}}""", ClaudeState.NeedsApproval);
        Assert.True(vm.KeyDeny());
        Assert.Equal("The user denied this tool call from the Isle notch.", (string?)Response()!["hookSpecificOutput"]!["decision"]!["message"]);
        Assert.Equal("proj · denied", Assert.Single(_host.Toasts).Text);
        Assert.False(vm.KeyDeny());
    }

    [Fact]
    public void Always_ReviewThenConfirm_SendsSuggestionsVerbatim()
    {
        var vm = Setup("permission", $$"""{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"npm run build"},"permission_suggestions":{{Sugg}}}""", ClaudeState.NeedsApproval);
        Assert.True(vm.CanRemember); Assert.True(vm.Keys!.HasAlways); Assert.False(vm.Keys.Reviewing);
        Assert.Equal("Needs your approval", vm.Headline);
        vm.Always.Execute(null);
        Assert.True(vm.Keys!.Reviewing);
        Assert.Equal("Don't ask again?", vm.Headline);
        Assert.Equal("Allow Bash(npm run *)", vm.CardLabel);
        Assert.Equal("Switch to acceptEdits mode · ", vm.CardSecondBold);
        Assert.Equal("this project · .claude/settings.local.json; this session", vm.CardSecond);
        Assert.True(vm.KeyDeny());                  // Back
        Assert.False(vm.Keys!.Reviewing); Assert.Empty(_host.Toasts);
        vm.Always.Execute(null);
        Assert.True(vm.KeyApprove());               // Confirm
        var d = Response()!["hookSpecificOutput"]!["decision"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Sugg), d["updatedPermissions"]));
        Assert.Equal("allow", (string?)d["behavior"]);
        Assert.Equal("proj · always allowed", Assert.Single(_host.Toasts).Text);
    }

    [Fact]
    public void DenyWithReason_SendsWordsAndHoldsKeys()
    {
        var vm = Setup("permission", """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"rm x"}}""", ClaudeState.NeedsApproval);
        vm.BeginDeny.Execute(null);
        Assert.True(vm.IsDenying); Assert.Null(vm.Keys);
        vm.SubmitDeny.Execute(null);                // empty: nothing
        Assert.Null(Response());
        vm.DenialText = "  use rg instead ";
        vm.CancelDeny.Execute(null);                // Esc keeps the draft
        Assert.False(vm.IsDenying); Assert.Equal("  use rg instead ", vm.DenialText);
        vm.BeginDeny.Execute(null); vm.SubmitDeny.Execute(null);
        Assert.Equal("The user denied this tool call from the Isle notch and said: use rg instead", (string?)Response()!["hookSpecificOutput"]!["decision"]!["message"]);
        Assert.Empty(_host.Toasts);                 // no toast for a typed denial
    }

    [Fact]
    public void Sound_FiresOnceOnNewApproval_NotAtLaunch()
    {
        var vm = Setup("permission", """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"ls"}}""", ClaudeState.NeedsApproval);
        var heard = new List<ClaudeSound>();
        vm.Sound += heard.Add;
        void Set(ClaudeState s) => File.WriteAllText(Path.Combine(_t.Paths.Sessions, "s1.json"),
            StatusStore.Serialize(new SessionStatus("s1", s, "proj", @"C:\proj", null, null, null, null, DateTimeOffset.UtcNow)));
        vm.RefreshNow(); Assert.Empty(heard);        // already waiting at launch: no chime
        Set(ClaudeState.Working); vm.RefreshNow(); Assert.Empty(heard);
        Set(ClaudeState.NeedsQuestion); vm.RefreshNow(); vm.RefreshNow();
        Assert.Equal(new[] { ClaudeSound.Alert }, heard);
        Set(ClaudeState.Done); vm.RefreshNow(); vm.RefreshNow();
        Assert.Equal(new[] { ClaudeSound.Alert, ClaudeSound.Done }, heard);
    }
}
