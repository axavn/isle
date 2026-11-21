// The public surface of IsleWin.Core. Every signature here is a contract the
// app, the Claude UI and the hook helper are built against: implement the
// bodies (splitting into more files is fine), never change a signature.
// See docs/PROTOCOL.md for the behaviour each one owes.

using System.Text.Json.Nodes;

namespace IsleWin.Core;

public enum ClaudeState { Disconnected, Idle, Working, NeedsApproval, NeedsQuestion, WaitingInput, Done, Failed, Compacting }

public enum IslandFace { Music, Claude }

/// Implemented by the app's island window; used by the Claude UI.
public interface IIslandHost
{
    void RequestKeyboard(bool wantsKeyboard);
    void RequestOpen(bool open);
    void ShowFace(IslandFace face);
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
    DateTimeOffset UpdatedAt);

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

