using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleWin.Core;

internal static class J
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static JsonNode? TryParse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json); } catch { return null; }
    }
    public static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    public static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
    public static string? Target(JsonNode? toolInput, int max)
    {
        foreach (var k in new[] { "file_path", "command", "pattern", "url", "description" })
            if (Str(toolInput, k) is { Length: > 0 } s) return Cut(s, max);
        return null;
    }
    public static string StateName(ClaudeState s) => s switch
    {
        ClaudeState.NeedsApproval => "needs_approval", ClaudeState.NeedsQuestion => "needs_question",
        ClaudeState.WaitingInput => "waiting_input", _ => s.ToString().ToLowerInvariant()
    };
    public static ClaudeState? ParseState(string? s) => s switch
    {
        "idle" => ClaudeState.Idle, "working" => ClaudeState.Working, "needs_approval" => ClaudeState.NeedsApproval,
        "needs_question" => ClaudeState.NeedsQuestion, "waiting_input" => ClaudeState.WaitingInput,
        "done" => ClaudeState.Done, "failed" => ClaudeState.Failed, "compacting" => ClaudeState.Compacting, _ => null
    };
    public static IEnumerable<string> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; } catch { return []; }
    }
    public static bool PidAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        // Replacing a file another process has open (without FILE_SHARE_DELETE) fails on Windows with
        // UnauthorizedAccessException or IOException until they let go, so retry briefly.
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when (i < 15 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(5 + i * 5); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }
    }

    /// Reads without blocking anyone else's atomic replace (FILE_SHARE_DELETE) or write.
    public static string ReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8, true);
        return r.ReadToEnd();
    }
}

public static class StatusStore
{
    public static string Serialize(SessionStatus s) => new JsonObject
    {
        ["session_id"] = s.SessionId, ["state"] = J.StateName(s.State), ["project"] = s.Project, ["cwd"] = s.Cwd,
        ["transcript_path"] = s.TranscriptPath, ["tool"] = s.Tool, ["tool_target"] = s.ToolTarget,
        ["error_type"] = s.ErrorType, ["updated_at"] = s.UpdatedAt.UtcDateTime.ToString("o"),
        ["subagents"] = s.Subagents is { Count: > 0 } a ? new JsonArray(a.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["type"] = x.Type }).ToArray()) : null,
        ["last_message"] = s.LastMessage,
    }.ToJsonString(J.Indented);

    public static SessionStatus? Parse(string json)
    {
        try
        {
            var n = J.TryParse(json);
            if (n is not JsonObject || J.Str(n, "session_id") is not { Length: > 0 } id) return null;
            var at = DateTimeOffset.TryParse(J.Str(n, "updated_at"), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UnixEpoch;
            return new SessionStatus(id, J.ParseState(J.Str(n, "state")) ?? ClaudeState.Idle, J.Str(n, "project"), J.Str(n, "cwd"),
                J.Str(n, "transcript_path"), J.Str(n, "tool"), J.Str(n, "tool_target"), J.Str(n, "error_type"), at,
                n["subagents"] is JsonArray arr ? arr.Select(x => new Subagent(J.Str(x, "id") ?? "", J.Str(x, "type") ?? "agent")).ToList() : null,
                J.Str(n, "last_message"));
        }
        catch { return null; }
    }

    public static IReadOnlyList<SessionStatus> ReadAll(IslePaths paths)
    {
        var list = new List<SessionStatus>();
        foreach (var f in J.Files(paths.Sessions, "*.json"))
            try { if (Parse(AtomicFile.ReadAllText(f)) is { } s) list.Add(s); } catch { }
        return list;
    }
}

public static class RegistryStore
{
    /// A status for a live session the hooks haven't reported (hooks not installed,
    /// or a session started before they were). Claude Code keeps `status` current itself.
    public static SessionStatus ToStatus(RegistryRecord r, DateTimeOffset now)
    {
        var s = r.Status?.ToLowerInvariant() ?? "";
        var state = s.Contains("busy") || s.Contains("work") || s.Contains("run") ? ClaudeState.Working
                  : s.Contains("wait") || s.Contains("block") ? ClaudeState.WaitingInput
                  : ClaudeState.Idle;
        var project = string.IsNullOrEmpty(r.Cwd) ? r.Name : Path.GetFileName(r.Cwd.TrimEnd('\\', '/'));
        return new SessionStatus(r.SessionId!, state, project, r.Cwd, null, null, null, null, now);
    }

    public static RegistryRecord? Parse(string json)
    {
        try
        {
            if (J.TryParse(json) is not JsonObject n) return null;
            return new RegistryRecord(int.TryParse(J.Str(n, "pid"), out var pid) ? pid : 0, J.Str(n, "sessionId"), J.Str(n, "cwd"),
                J.Str(n, "status"), J.Str(n, "procStart"), J.Str(n, "kind"), J.Str(n, "name"));
        }
        catch { return null; }
    }

    public static IReadOnlyList<RegistryRecord> ReadAll(IslePaths paths)
    {
        var list = new List<RegistryRecord>();
        foreach (var f in J.Files(paths.ClaudeSessions, "*.json"))
            try { if (Parse(AtomicFile.ReadAllText(f)) is { } r) list.Add(r); } catch { }
        return list;
    }

    public static bool IsAlive(RegistryRecord r)
    {
        if (r.Pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(r.Pid);
            if (p.HasExited) return false;
            if (!long.TryParse(r.ProcStart, out var ft)) return true;
            try { return Math.Abs(p.StartTime.ToFileTimeUtc() - ft) <= 10_000_000; }
            catch { return true; } // start time unreadable (access denied): existence is all we can prove
        }
        catch { return false; }
    }
}

public static class SessionRanker
{
    public static int Rank(ClaudeState s) => s switch
    {
        ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion => 0, ClaudeState.Failed => 1,
        ClaudeState.Working or ClaudeState.Compacting => 2, ClaudeState.WaitingInput => 3,
        ClaudeState.Done => 4, _ => 5
    };

    public static SessionStatus? Select(IReadOnlyList<SessionStatus> statuses, IReadOnlySet<string>? liveIds,
                                        string? currentId, string? pinnedId, DateTimeOffset now)
    {
        var live = statuses.Where(s => liveIds != null ? liveIds.Contains(s.SessionId) : now - s.UpdatedAt <= TimeSpan.FromMinutes(30)).ToList();
        if (live.Count == 0) return null;
        if (pinnedId != null && live.FirstOrDefault(s => s.SessionId == pinnedId) is { } pinned) return pinned;
        var best = live.OrderBy(s => Rank(s.State)).ThenByDescending(s => s.UpdatedAt).First();
        if (currentId != null && live.FirstOrDefault(s => s.SessionId == currentId) is { } cur && Rank(cur.State) <= Rank(best.State)) return cur;
        return best;
    }
}

public static class RequestStore
{
    public static IReadOnlyList<PendingRequest> ReadPending(IslePaths paths)
    {
        var list = new List<PendingRequest>();
        foreach (var f in J.Files(paths.Requests, "*.json"))
        {
            if (f.EndsWith(".response.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.Exists(Path.ChangeExtension(f, ".response.json"))) continue;
                if (J.TryParse(AtomicFile.ReadAllText(f)) is not JsonObject n) continue;
                var id = J.Str(n, "request_id") ?? Path.GetFileNameWithoutExtension(f);
                var kind = J.Str(n, "kind") == "question" ? RequestKind.Question : RequestKind.Permission;
                var at = DateTimeOffset.TryParse(J.Str(n, "created_at"), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : File.GetLastWriteTimeUtc(f);
                var input = n["payload"]?["tool_input"];
                var qs = new List<Question>();
                if (input?["questions"] is JsonArray arr)
                    foreach (var q in arr)
                    {
                        var opts = new List<QuestionOption>();
                        if (q?["options"] is JsonArray oa)
                            foreach (var o in oa) if (J.Str(o, "label") is { } l) opts.Add(new(l, J.Str(o, "description")));
                        if (J.Str(q, "question") is { } text)
                            qs.Add(new(text, J.Str(q, "header"), opts, q?["multiSelect"] is JsonValue mv && mv.TryGetValue<bool>(out var b) && b));
                    }
                var toolName = J.Str(n["payload"], "tool_name");
                // A permission prompt for AskUserQuestion is the terminal's question UI coming up, not an approval.
                if (kind == RequestKind.Permission && toolName == "AskUserQuestion") continue;
                list.Add(new(id, kind, J.Str(n, "session_id") ?? "", toolName, J.Target(input, 200), qs, at, f));
            }
            catch { }
        }
        return list.OrderBy(r => r.CreatedAt).ToList();
    }

    public static void WriteResponse(IslePaths paths, string requestId, string json) =>
        AtomicFile.WriteAllText(Path.Combine(paths.Requests, requestId + ".response.json"), json);

    /// The whole hook payload of a request, re-read from its file (a deep copy).
    public static JsonObject? ReadPayload(PendingRequest request)
    {
        try { return J.TryParse(AtomicFile.ReadAllText(request.FilePath))?["payload"] is JsonObject o ? (JsonObject)o.DeepClone() : null; }
        catch { return null; }
    }

    public static JsonObject? ReadToolInput(PendingRequest request)
    {
        try { return J.TryParse(AtomicFile.ReadAllText(request.FilePath))?["payload"]?["tool_input"] is JsonObject o ? (JsonObject)o.DeepClone() : null; }
        catch { return null; }
    }
}

public static class ResponseBuilder
{
    public static string Allow() => new JsonObject { ["hookSpecificOutput"] = new JsonObject
    {
        ["hookEventName"] = "PermissionRequest", ["decision"] = new JsonObject { ["behavior"] = "allow" }
    } }.ToJsonString();

    public static string Deny(string message) => new JsonObject { ["hookSpecificOutput"] = new JsonObject
    {
        ["hookEventName"] = "PermissionRequest", ["decision"] = new JsonObject { ["behavior"] = "deny", ["message"] = message }
    } }.ToJsonString();

    static readonly JsonSerializerOptions CompactJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Decision(JsonObject decision) => new JsonObject { ["hookSpecificOutput"] = new JsonObject
    {
        ["hookEventName"] = "PermissionRequest", ["decision"] = decision
    } }.ToJsonString(CompactJson);

    /// Approving a plan: Claude Code ignores a bare allow for ExitPlanMode; the tool input must come back as `updatedInput`.
    public static string AllowPlan(JsonObject toolInput) =>
        Decision(new JsonObject { ["behavior"] = "allow", ["updatedInput"] = toolInput.DeepClone() });

    /// "Always...": allow, and hand Claude Code's own `permission_suggestions` back verbatim as `updatedPermissions`.
    public static string AllowAlways(JsonNode suggestions) =>
        Decision(new JsonObject { ["behavior"] = "allow", ["updatedPermissions"] = suggestions.DeepClone() });

    /// The message Claude is told on a denial: attributed, with the user's words when they typed some.
    public static string DenyMessage(bool isPlan, string? words = null)
    {
        var s = isPlan ? "The user rejected this plan from the Isle notch" : "The user denied this tool call from the Isle notch";
        return words?.Trim() is { Length: > 0 } w ? $"{s} and said: {w}" : s + ".";
    }

    /// No decision: the hook prints this, and Claude Code shows its own prompt.
    public static string Terminal() => "{}";

    /// The answer string for one question: a multi-select's labels in the order offered, comma-joined.
    public static string JoinPicks(Question q, IReadOnlySet<string> picked) =>
        string.Join(", ", q.Options.Select(o => o.Label).Where(picked.Contains));

    public static string Answers(JsonObject toolInput, IReadOnlyDictionary<string, string> answers)
    {
        var input = (JsonObject)toolInput.DeepClone();
        var a = new JsonObject();
        foreach (var kv in answers) a[kv.Key] = kv.Value;
        input["answers"] = a;
        return new JsonObject { ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "allow", ["updatedInput"] = input
        } }.ToJsonString();
    }
}

public static class AnsweringMarker
{
    public static void Write(IslePaths paths, int pid) => AtomicFile.WriteAllText(paths.AnsweringMarker, pid.ToString());
    public static void Remove(IslePaths paths) { try { File.Delete(paths.AnsweringMarker); } catch { } }
    public static bool IsActive(IslePaths paths)
    {
        try { return int.TryParse(AtomicFile.ReadAllText(paths.AnsweringMarker).Trim(), out var pid) && J.PidAlive(pid); }
        catch { return false; }
    }
}

public static class HookInstaller
{
    static bool IsIsle(JsonNode? h) => J.Str(h, "command")?.Contains("isle-cli") == true;

    static JsonObject Strip(JsonNode? existing)
    {
        var root = existing as JsonObject ?? new JsonObject();
        if (root["hooks"] is not JsonObject hooks) return root;
        foreach (var ev in hooks.ToList())
        {
            if (ev.Value is not JsonArray groups) continue;
            foreach (var g in groups.ToList())
            {
                if (g?["hooks"] is not JsonArray hs) continue;
                var before = hs.Count;
                foreach (var h in hs.ToList()) if (IsIsle(h)) hs.Remove(h);
                if (hs.Count == 0 && before > 0) groups.Remove(g);
            }
            if (groups.Count == 0) hooks.Remove(ev.Key);
        }
        if (hooks.Count == 0) root.Remove("hooks");
        return root;
    }

    public static string Merge(string? existingSettingsJson, string cliExePath)
    {
        var root = Strip(J.TryParse(existingSettingsJson));
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var exe = "\"" + cliExePath.Replace('\\', '/') + "\"";
        void Add(string ev, string verb, string? matcher = null, int? timeout = null)
        {
            var h = new JsonObject { ["type"] = "command", ["command"] = $"{exe} {verb}" };
            if (timeout != null) h["timeout"] = timeout;
            var g = new JsonObject();
            if (matcher != null) g["matcher"] = matcher;
            g["hooks"] = new JsonArray(h);
            if (hooks[ev] is not JsonArray arr) hooks[ev] = arr = new JsonArray();
            arr.Add(g);
        }
        Add("SessionStart", "set-state idle");
        Add("UserPromptSubmit", "set-state working");
        Add("PreToolUse", "set-state working");
        Add("PreToolUse", "ask-question", "AskUserQuestion", 60);
        Add("PermissionRequest", "ask", timeout: 330);
        Add("PostToolUse", "set-state working");
        Add("PostToolUseFailure", "set-state working");
        Add("Notification", "notify");
        Add("Stop", "set-state done");
        Add("StopFailure", "fail");
        Add("PreCompact", "set-state compacting");
        Add("SubagentStart", "subagent-start");
        Add("SubagentStop", "subagent-stop");
        Add("SessionEnd", "end");
        root["hooks"] = hooks;
        return root.ToJsonString(J.Indented);
    }

    public static string Unmerge(string existingSettingsJson) => Strip(J.TryParse(existingSettingsJson)).ToJsonString(J.Indented);

    public static bool IsInstalled(IslePaths paths)
    {
        try { return File.Exists(paths.ClaudeSettings) && AtomicFile.ReadAllText(paths.ClaudeSettings).Contains("isle-cli"); }
        catch { return false; }
    }

    public static void Install(IslePaths paths, string cliSourceDir)
    {
        string? existing = File.Exists(paths.ClaudeSettings) ? AtomicFile.ReadAllText(paths.ClaudeSettings) : null;
        // Refuse to rewrite a settings file we can't parse: that would destroy the user's config.
        if (!string.IsNullOrWhiteSpace(existing) && J.TryParse(existing) is not JsonObject)
            throw new InvalidOperationException("settings.json is not valid JSON; not modifying it.");
        Directory.CreateDirectory(paths.Bin);
        foreach (var f in Directory.GetFiles(cliSourceDir).Where(f =>
            Path.GetFileName(f).StartsWith("isle-cli", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("IsleWin.Core.dll", StringComparison.OrdinalIgnoreCase)))
        {
            var dest = Path.Combine(paths.Bin, Path.GetFileName(f));
            for (var i = 0; ; i++)
            {
                try { File.Copy(f, dest, overwrite: true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(250); }
            }
        }
        var backup = paths.ClaudeSettings + ".isle-backup";
        if (existing != null && !File.Exists(backup)) File.WriteAllText(backup, existing);
        AtomicFile.WriteAllText(paths.ClaudeSettings, Merge(existing, Path.Combine(paths.Bin, "isle-cli.exe")));
    }

    public static void Uninstall(IslePaths paths)
    {
        if (!File.Exists(paths.ClaudeSettings)) return;
        var text = AtomicFile.ReadAllText(paths.ClaudeSettings);
        if (J.TryParse(text) is not JsonObject) return;
        AtomicFile.WriteAllText(paths.ClaudeSettings, Unmerge(text));
    }
}

public static class TranscriptReader
{
    static IEnumerable<JsonNode> Tail(string path, int maxBytes)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            var n = 0;
            while (n < buf.Length) { var r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
            var text = Encoding.UTF8.GetString(buf, 0, n);
            if (start > 0) { var nl = text.IndexOf('\n'); text = nl < 0 ? "" : text[(nl + 1)..]; }
            lines.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch { }
        foreach (var l in lines) if (J.TryParse(l) is JsonObject o) yield return o;
    }

    static string? Role(JsonNode n) => J.Str(n["message"], "role") ?? J.Str(n, "type");

    static string Text(JsonNode n)
    {
        var c = n["message"]?["content"];
        if (c is JsonValue v) return v.TryGetValue<string>(out var s) ? s : "";
        if (c is not JsonArray arr) return "";
        return string.Join("\n", arr.Where(b => J.Str(b, "type") == "text").Select(b => J.Str(b, "text")).Where(t => !string.IsNullOrEmpty(t)));
    }

    public static string? LastAssistantText(string transcriptPath, int maxBytes = 65536)
    {
        string? last = null;
        foreach (var n in Tail(transcriptPath, maxBytes))
            if (Role(n) == "assistant" && Text(n) is { Length: > 0 } t) last = t;
        return last;
    }

    /// The usage limit the session is stopped on, if its latest assistant message is Claude Code's limit
    /// notice; with the "continue automatically at" time if Claude Code set one after it.
    public static UsageLimit? CurrentLimit(string transcriptPath, int maxBytes = 65536)
    {
        UsageLimit? limit = null;
        foreach (var n in Tail(transcriptPath, maxBytes))
        {
            var role = Role(n);
            if (role == "assistant" && Text(n) is { Length: > 0 } t) limit = ClaudeText.ParseLimit(t);
            else if (limit != null && J.Str(n, "type") == "system" && ClaudeText.ParseContinue(J.Str(n, "content")) is { } at) limit = limit with { ContinuesAt = at };
        }
        return limit;
    }

    public static bool ContainsUserText(string transcriptPath, string text, int maxBytes = 65536) =>
        Tail(transcriptPath, maxBytes).Any(n => Role(n) == "user" && Text(n).Contains(text, StringComparison.Ordinal));
}

public static class HookHandler
{
    public static string Run(string verb, string? arg, string stdinJson, IslePaths paths)
    {
        try { return RunCore(verb, arg, stdinJson, paths); }
        catch (Exception e)
        {
            // Never fail the hook, but leave a trace: a silent bridge is undebuggable.
            try { File.AppendAllText(Path.Combine(paths.Root, "isle-cli.log"), $"{DateTimeOffset.Now:o} {verb} {e}\n"); } catch { }
            return "";
        }
    }

    static string RunCore(string verb, string? arg, string stdin, IslePaths paths)
    {
        var n = J.TryParse(stdin) as JsonObject ?? new JsonObject();
        var id = J.Str(n, "session_id");
        if (string.IsNullOrEmpty(id)) return "";
        var file = Path.Combine(paths.Sessions, id + ".json");
        var prev = File.Exists(file) ? StatusStore.Parse(AtomicFile.ReadAllText(file)) : null;
        var tool = J.Str(n, "tool_name");
        var isPre = J.Str(n, "hook_event_name") == "PreToolUse";

        // Subagents outlive state changes (a background agent outlives its turn); the last message only means
        // something while the turn is over, so any other write clears it.
        void Write(ClaudeState state, string? t = null, string? target = null, string? err = null,
                   IReadOnlyList<Subagent>? subs = null, string? lastMessage = null, DateTimeOffset? at = null)
        {
            var cwd = J.Str(n, "cwd") ?? prev?.Cwd;
            var project = string.IsNullOrEmpty(cwd) ? prev?.Project : Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(project)) project = prev?.Project;
            AtomicFile.WriteAllText(file, StatusStore.Serialize(new SessionStatus(id, state, project, cwd,
                J.Str(n, "transcript_path") ?? prev?.TranscriptPath, t, target, err, at ?? DateTimeOffset.UtcNow,
                subs ?? prev?.Subagents, lastMessage)));
        }

        switch (verb)
        {
            case "set-state":
                var st = J.ParseState(arg) ?? ClaudeState.Working;
                // ask-question runs in parallel for AskUserQuestion and must not be overwritten.
                if (st == ClaudeState.Working && isPre && tool == "AskUserQuestion") st = ClaudeState.NeedsQuestion;
                if (st == ClaudeState.Working && isPre && tool != null) Write(st, tool, J.Target(n["tool_input"], 120));
                else if (st == ClaudeState.Done) Write(st, lastMessage: J.Str(n, "last_assistant_message") is { } m ? J.Cut(m, 300) : null);
                else Write(st);
                return "";
            case "subagent-start":
            case "subagent-stop":
            {
                var list = (prev?.Subagents ?? Array.Empty<Subagent>()).ToList();
                var aid = J.Str(n, "agent_id") ?? "";
                if (verb == "subagent-start") { if (!list.Any(a => a.Id == aid)) list.Add(new Subagent(aid, J.Str(n, "agent_type") is { Length: > 0 } ty ? ty : "agent")); }
                else list.RemoveAll(a => a.Id == aid);
                // the session's own state, tool and time are left as they were: this only edits the list
                Write(prev?.State ?? ClaudeState.Working, prev?.Tool, prev?.ToolTarget, prev?.ErrorType, list, prev?.LastMessage, prev?.UpdatedAt);
                return "";
            }
            case "notify":
                var nt = J.Str(n, "notification_type");
                // A question is the more specific signal; the notification for the same prompt must not downgrade it.
                if (prev?.State == ClaudeState.NeedsQuestion && nt is "permission_prompt" or "idle_prompt" or null) return "";
                var ns = nt == "permission_prompt" ? ClaudeState.NeedsApproval : nt == "idle_prompt" ? ClaudeState.WaitingInput : (ClaudeState?)null;
                if (ns != null) Write(ns.Value, prev?.Tool, prev?.ToolTarget);
                return "";
            case "fail":
                Write(ClaudeState.Failed, err: J.Str(n, "error_type") ?? J.Str(n, "error"));
                return "";
            case "end":
                try { File.Delete(file); } catch { }
                return "";
            case "ask":
                // Claude Code raises a PermissionRequest for AskUserQuestion once ask-question gave no answer
                // (timeout, "Answer in terminal"): it is the same question in the terminal, not an approval.
                if (tool == "AskUserQuestion") { Write(ClaudeState.NeedsQuestion, tool, J.Target(n["tool_input"], 120)); return ""; }
                Write(ClaudeState.NeedsApproval, tool, J.Target(n["tool_input"], 120));
                return Block(paths, id, "permission", n, TimeSpan.FromSeconds(300));
            case "ask-question":
                Write(ClaudeState.NeedsQuestion, tool, J.Target(n["tool_input"], 120));
                // nothing to pick from: only the terminal can answer it
                if (!(n["tool_input"]?["questions"] is JsonArray { Count: > 0 } qa && qa.All(q => q?["options"] is JsonArray { Count: > 0 }))) return "";
                return Block(paths, id, "question", n, TimeSpan.FromSeconds(45));
            default: return "";
        }
    }

    static string Block(IslePaths paths, string sessionId, string kind, JsonObject payload, TimeSpan timeout)
    {
        if (!AnsweringMarker.IsActive(paths)) return "";
        var rid = Guid.NewGuid().ToString("N");
        var req = Path.Combine(paths.Requests, rid + ".json");
        var resp = Path.Combine(paths.Requests, rid + ".response.json");
        AtomicFile.WriteAllText(req, new JsonObject
        {
            ["request_id"] = rid, ["kind"] = kind, ["session_id"] = sessionId,
            ["created_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"), ["payload"] = payload.DeepClone()
        }.ToJsonString());
        try
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                if (File.Exists(resp))
                    try { return AtomicFile.ReadAllText(resp); } catch (IOException) { }
                Thread.Sleep(100);
                // The app died mid-wait: fall back to Claude Code's own prompt.
                if (!AnsweringMarker.IsActive(paths)) return "";
            }
            return "";
        }
        finally
        {
            try { File.Delete(req); } catch { }
            try { File.Delete(resp); } catch { }
        }
    }
}
