using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleWin.Core;

/// What "Always..." would remember, in words: the changes and where they would be saved.
public sealed record RememberReview(IReadOnlyList<string> Changes, string Scope);

/// A permission request as the approval card shows it (Isle's ClaudeApproval): the tool's label, its subject,
/// an optional count note, and the "Always..." review when every suggestion can be spelled out.
public sealed record ApprovalInfo(bool IsPlan, string Label, string Subject, string? Note, RememberReview? Remember);

public static partial class ClaudeText
{
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// The card for a hook payload (`tool_name`, `tool_input`, `cwd`, `mcp_server`, `permission_suggestions`).
    public static ApprovalInfo Approval(JsonNode? payload, string home)
    {
        var tool = J.Str(payload, "tool_name") ?? "";
        var input = payload?["tool_input"] as JsonObject;
        var cwd = J.Str(payload, "cwd");
        var server = J.Str(payload?["mcp_server"], "name");
        bool plan = tool == "ExitPlanMode";
        return new ApprovalInfo(plan, ToolLabel(tool, server), Subject(tool, input, cwd, home) ?? "", Note(tool, input),
            plan ? null : Remember(payload?["permission_suggestions"], cwd, home));
    }

    /// The tool's name as the card shows it: MCP "server › tool", ExitPlanMode "Plan".
    public static string ToolLabel(string tool, string? mcpServer = null)
    {
        if (tool == "ExitPlanMode") return "Plan";
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return tool;
        var rest = tool[5..];
        int i = rest.IndexOf("__", StringComparison.Ordinal);
        if (i < 0) return tool;
        var name = rest[(i + 2)..];
        return $"{(string.IsNullOrEmpty(mcpServer) ? rest[..i] : mcpServer)} › {name}";
    }

    /// What the call is about, first match winning; a newline reads as " ↵ ".
    public static string? Subject(string tool, JsonObject? input, string? cwd, string home)
    {
        string? s;
        if (tool == "ExitPlanMode") s = PlanOutline(J.Str(input, "plan"));
        else if (tool.StartsWith("mcp__", StringComparison.Ordinal)) s = Arguments(input);
        else if (J.Str(input, "file_path") is { Length: > 0 } fp) s = DisplayPath(fp, cwd, home);
        else if (J.Str(input, "notebook_path") is { Length: > 0 } np) s = DisplayPath(np, cwd, home);
        else
        {
            s = null;
            foreach (var k in new[] { "command", "url", "query", "pattern", "path", "description", "prompt" })
                if (J.Str(input, k) is { Length: > 0 } v) { s = v; break; }
            s ??= Arguments(input);
        }
        return s?.Replace("\r\n", "\n").Replace("\n", " ↵ ");
    }

    static string? Arguments(JsonObject? input) => input is not { Count: > 0 } ? null
        : string.Join(", ", input.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + (kv.Value?.ToJsonString(Compact) ?? "null")));

    /// The plan's lines, heading marks off, blank lines dropped, joined with newlines.
    public static string PlanOutline(string? plan)
    {
        if (plan == null) return "";
        var lines = plan.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    static int Lines(string? x) => string.IsNullOrEmpty(x) ? 0 : x.Split('\n').Length - (x.EndsWith('\n') ? 1 : 0);
    static string Plural(int n, string one) => n == 1 ? $"{n} {one}" : $"{n} {one}s";

    /// The count line under an edit's path: "−1 +1 lines", "3 edits", "12 lines", "replace · cell abc".
    public static string? Note(string tool, JsonObject? input)
    {
        switch (tool)
        {
            case "Edit":
            {
                var s = $"−{Lines(J.Str(input, "old_string"))} +{Lines(J.Str(input, "new_string"))} lines";
                return input?["replace_all"] is JsonValue v && v.TryGetValue<bool>(out var b) && b ? s + " · every occurrence" : s;
            }
            case "MultiEdit": return input?["edits"] is JsonArray e ? Plural(e.Count, "edit") : null;
            case "Write":
                var c = J.Str(input, "content");
                return string.IsNullOrEmpty(c) ? "empty file" : Plural(Lines(c), "line");
            case "NotebookEdit":
                var mode = J.Str(input, "edit_mode") ?? "replace";
                return J.Str(input, "cell_id") is { Length: > 0 } cell ? $"{mode} · cell {cell}" : mode;
            default: return null;
        }
    }

    /// A path as the card shows it: relative inside the session's folder, "~" for home, else whole.
    public static string DisplayPath(string path, string? cwd, string? home)
    {
        static string N(string s) => s.Replace('\\', '/');
        static string? Under(string path, string? root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            var r = N(root).TrimEnd('/');
            var p = N(path);
            return p.Length > r.Length + 1 && p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ? path[(r.Length + 1)..] : null;
        }
        if (Under(path, cwd) is { } rel) return rel;
        if (!string.IsNullOrEmpty(home))
        {
            var h = N(home).TrimEnd('/');
            if (string.Equals(N(path).TrimEnd('/'), h, StringComparison.OrdinalIgnoreCase)) return "~";
            if (Under(path, home) != null) return "~" + path[h.Length..];
        }
        return path;
    }

    static string? ScopeLabel(string? destination) => destination switch
    {
        "session" or "cliArg" => "this session",
        "localSettings" => "this project · .claude/settings.local.json",
        "projectSettings" => "this project · .claude/settings.json",
        "userSettings" => "every project · ~/.claude/settings.json",
        _ => null,
    };

    /// Decodes `permission_suggestions` for "Always...". All or nothing: a suggestion that can't be spelled
    /// exactly (or none at all) is null, and the pill isn't offered.
    public static RememberReview? Remember(JsonNode? raw, string? cwd, string home)
    {
        if (raw is not JsonArray { Count: > 0 } arr) return null;
        var rules = new List<string>(); var extra = new List<string>(); var scopes = new List<string>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o || J.Str(o, "type") is not { } type || ScopeLabel(J.Str(o, "destination")) is not { } scope) return null;
            switch (type)
            {
                case "addRules":
                    if (J.Str(o, "behavior") != "allow" || o["rules"] is not JsonArray { Count: > 0 } rs) return null;
                    foreach (var r in rs)
                    {
                        if (J.Str(r, "toolName") is not { Length: > 0 } name) return null;
                        rules.Add(r?["ruleContent"] is JsonValue cv && cv.TryGetValue<string>(out var content) ? $"{name}({content})" : name);
                    }
                    break;
                case "setMode":
                    if (J.Str(o, "mode") is not { Length: > 0 } mode) return null;
                    extra.Add($"Switch to {mode} mode");
                    break;
                case "addDirectories":
                    if (o["directories"] is not JsonArray { Count: > 0 } ds) return null;
                    var dirs = new List<string>();
                    foreach (var d in ds) { if (d is JsonValue dv && dv.TryGetValue<string>(out var dir)) dirs.Add(DisplayPath(dir, null, home)); else return null; }
                    extra.Add("Allow access to " + string.Join(", ", dirs));
                    break;
                default: return null;
            }
            if (!scopes.Contains(scope)) scopes.Add(scope);
        }
        var changes = new List<string>();
        if (rules.Count > 0) changes.Add("Allow " + string.Join(", ", rules));
        changes.AddRange(extra);
        return new RememberReview(changes, string.Join("; ", scopes));
    }
}
