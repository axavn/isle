// The public surface of IsleWin.Core. Every signature here is a contract the
// app, the Claude UI and the hook helper are built against: implement the
// bodies (splitting into more files is fine), never change a signature.
// See docs/PROTOCOL.md for the behaviour each one owes.

using System.Text.Json.Nodes;

namespace IsleWin.Core;

public enum ClaudeState { Disconnected, Idle, Working, NeedsApproval, NeedsQuestion, WaitingInput, Done, Failed, Compacting }

/// The island's faces, in Isle's `availableTabs` order (the Home grid's and the swipe's): Music, Claude,
/// Pomodoro (opt-in), Shelf (only while it holds something). Home is the absence of a face.
public enum IslandFace { Music, Claude, Pomodoro, Shelf }

/// A toast on the collapsed pill (IslandToast.swift): a glyph and a few words in place of the pill's
/// content for 4 s. A toast of the same `Kind` replaces the one showing; others queue (2 at most).
/// `Glyph` is a Segoe Fluent Icons character (IsleStyle); `Tint` a "#RRGGBB" for glyph and text, null = white.
public sealed record IslandToast(string Kind, string Text, string Glyph, string? Tint = null);

/// Implemented by the app's island window; used by the Claude UI.
public interface IIslandHost
{
    void RequestKeyboard(bool wantsKeyboard);
    void RequestOpen(bool open);
    void ShowFace(IslandFace face);
    void Toast(IslandToast toast);
}

/// Every path the bridge uses. `Default` is the real user profile; tests pass a temp folder.
public sealed class IslePaths
{
    public IslePaths(string userHome) { UserHome = userHome; }
    public static IslePaths Default { get; } = new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string UserHome { get; }
    public string Root => Path.Combine(UserHome, ".isle");
    public string Sessions => Path.Combine(Root, "sessions");
    public string Requests => Path.Combine(Root, "requests");
    public string AnsweringMarker => Path.Combine(Root, "run", "answering");
    public string Bin => Path.Combine(Root, "bin");
    public string ClaudeSessions => Path.Combine(UserHome, ".claude", "sessions");
    public string ClaudeSettings => Path.Combine(UserHome, ".claude", "settings.json");
}

public sealed record SessionStatus(
    string SessionId,
    ClaudeState State,
    string? Project,
    string? Cwd,
    string? TranscriptPath,
    string? Tool,
    string? ToolTarget,
    string? ErrorType,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Subagent>? Subagents = null,
    string? LastMessage = null);

/// A subagent the session has running, from `SubagentStart` until its `SubagentStop`.
public sealed record Subagent(string Id, string Type);

/// One of Claude Code's own `~/.claude/sessions/<pid>.json` records.
public sealed record RegistryRecord(int Pid, string? SessionId, string? Cwd, string? Status, string? ProcStart, string? Kind, string? Name);

public enum RequestKind { Permission, Question }

public sealed record QuestionOption(string Label, string? Description);

public sealed record Question(string Text, string? Header, IReadOnlyList<QuestionOption> Options, bool MultiSelect);

/// A hook that is blocked waiting for the island to answer.
public sealed record PendingRequest(
    string RequestId,
    RequestKind Kind,
    string SessionId,
    string? ToolName,
    string? ToolSummary,              // e.g. the Bash command or the file path, max 200 chars
    IReadOnlyList<Question> Questions, // empty for a permission
    DateTimeOffset CreatedAt,
    string FilePath);

