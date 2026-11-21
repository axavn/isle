# Isle for Windows: prototype contract

A Windows port of the ideas in Isle (github.com/matthewhamilton3141/isle, MIT).
Prototype scope: a top-centre overlay "island" with **Spotify** (now playing,
controls, waveform) and **Claude Code sessions** (status, approve, answer
questions, done summary, jump, reply-by-paste).

Stack: C# / .NET 10, WPF, Windows 11. No Calendar, Bluetooth, Shelf or Pomodoro.

## Projects and who owns them

| Project | Kind | Owner |
|---|---|---|
| `src/IsleWin.Core` | net10.0 class library. Pure logic, no UI. `Api.cs` holds every public signature | Bridge agent |
| `src/IsleCli` | net10.0 console app, assembly `isle-cli`. Hook helper; a thin wrapper over `HookHandler` | Bridge agent |
| `tests/IsleWin.Tests` | xUnit tests for Core | Bridge agent |
| `src/IsleWin.Claude` | WPF class library. `ClaudeViewModel`, `ClaudeMiniView`, `ClaudeView`, terminal bridge | Claude UI agent |
| `src/IsleWin` | WPF app. Overlay window, tray icon, Spotify, waveform, music views | Overlay agent |

Rules for every agent:
- Edit only your own projects. Never change a public signature in `Api.cs` or
  the placeholder public surface of `IsleWin.Claude`; implement behind it.
- Another agent may be building at the same time. If a build fails with a file
  lock ("being used by another process"), wait a few seconds and retry. Ignore
  compile errors in files you don't own.
- Keep code compact and commented only where the reason isn't obvious.

## File protocol (all under `%USERPROFILE%\.isle\`)

Every write is **atomic**: write `<name>.tmp-<guid>`, then
`File.Move(tmp, dest, overwrite: true)` (`AtomicFile.WriteAllText`).

| Path | Writer → reader | Content |
|---|---|---|
| `sessions\<session_id>.json` | hook → app | `SessionStatus` as JSON (snake_case, see below) |
| `requests\<request_id>.json` | hook → app | `{request_id, kind:"permission"\|"question", session_id, created_at, payload:<raw hook stdin>}` |
| `requests\<request_id>.response.json` | app → hook | The exact JSON the hook prints to stdout |
| `run\answering` | app → hook | The app's pid as text. Present only while the app runs |
| `bin\` | installer | `isle-cli.exe` and its files |

`sessions\<id>.json` fields: `session_id, state, project, cwd, transcript_path,
tool, tool_target, error_type, updated_at` (ISO-8601 UTC). `state` is one of
`idle, working, needs_approval, needs_question, waiting_input, done, failed,
compacting`. `project` = last folder name of `cwd`.

## Hook verbs (`isle-cli <verb>`, hook JSON on stdin)

Hook stdin always has `session_id, transcript_path, cwd, hook_event_name`, and
for tool events `tool_name, tool_input, tool_use_id`. Read every field
defensively: missing or extra fields must never throw.

| Hook event | Command | Effect |
|---|---|---|
| SessionStart | `set-state idle` | write status |
| UserPromptSubmit, PostToolUse, PostToolUseFailure | `set-state working` | clear tool |
| PreToolUse (all tools) | `set-state working` | tool = `tool_name`, tool_target = first of `tool_input.file_path`, `command`, `pattern`, `url`, `description` (max 120 chars) |
| PreToolUse, matcher `AskUserQuestion`, timeout 60 | `ask-question` | blocking, see below |
| PermissionRequest, timeout 330 | `ask` | blocking, see below |
| Notification | `notify` | `notification_type` `permission_prompt` → needs_approval; `idle_prompt` → waiting_input; else no change |
| Stop | `set-state done` | |
| StopFailure | `fail` | failed, error_type = `error_type` or `error` field |
| PreCompact | `set-state compacting` | |
| SessionEnd | `end` | delete the session file |

Hook commands are written as `"C:/Users/<you>/.isle/bin/isle-cli.exe" <verb>`
(forward slashes, quoted) so they work in Git Bash and cmd alike. Hook entries
are recognised as Isle's by the substring `isle-cli` in `command`.

**Blocking verbs** (`ask`, `ask-question`): set the status (needs_approval or
needs_question), then only if `run\answering` exists and its pid is alive:
write the request file, poll for `<id>.response.json` every 100 ms for up to
300 s (`ask`) or 45 s (`ask-question`), print its content, delete both files,
exit 0. On timeout or a dead marker: print nothing, delete the request, exit 0,
so Claude Code falls back to its own prompt. The helper **always exits 0**,
and never blocks when the app isn't running.

## Response payloads (built by `ResponseBuilder`)

- Allow: `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`
- Deny: `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"<text>"}}}`
  (`decision` is an object, never a string)
- Answers: `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":<tool_input from the request, verbatim, plus "answers":{"<question text>":"<chosen label>"}>}}`

## Claude Code's own records (read-only)

`%USERPROFILE%\.claude\sessions\<pid>.json`, verified on this machine
(Claude Code 2.1.330):
`{"pid":23812,"sessionId":"…","cwd":"C:\\…","procStart":"134397648446605854",
"kind":"interactive","entrypoint":"cli","name":"math115-82","status":"idle","updatedAt":1795291718274}`.
`procStart` is the process start time as a Windows FILETIME (UTC). Alive =
process exists **and** its `StartTime.ToFileTimeUtc()` is within 1 s of
`procStart` (pid reuse guard). Undocumented: every field optional; a file
that won't parse is skipped.

## Session ranking (`SessionRanker`)

Live = has a registry record whose process is alive (match on `sessionId`),
or, when no registry records exist at all, any status file updated in the last
30 min. Rank: needs_approval / needs_question 0, failed 1, working / compacting
2, waiting_input 3, done 4, idle 5. A pinned session wins. Otherwise keep the
current session while it is live and its rank ≤ the best rank; else take the
best, ties broken by the newest `updated_at`.

## Island host contract (`IIslandHost`, implemented by the app's window)

- `RequestKeyboard(true)` makes the island focusable so a TextBox can take
  keys; `false` gives focus back.
- `RequestOpen(true)` opens the island on its own (an approval or question);
  `false` releases it.
- `ShowFace(IslandFace.Claude)` switches the expanded island to that face.

## Reply by console input (`IsleWin.Claude.TerminalBridge`)

Only CLI sessions are targeted: registry records whose `entrypoint` contains
`desktop` (Claude desktop app) have no terminal and are ignored.

Send: `AttachConsole(pid)` to the registry `pid`'s console (process-wide, so
one attach at a time, with Ctrl+C ignored while attached), open `CONIN$`, and
`WriteConsoleInputW` the text as key-down/key-up records (newlines folded to
spaces), then 150 ms later Enter on its own. `FreeConsole` after each write.
No clipboard, no focus change: it reaches the session even when its terminal
tab is in the background or minimised. Then confirm delivery: watch the
transcript tail for a user turn containing the text.

Jump: attach the same way, `GetConsoleWindow()`, then `GetAncestor(…,
GA_ROOTOWNER)` is the terminal window (Windows Terminal / VS Code own the
ConPTY pseudo-window; classic conhost is its own window). Walking parent pids
does not work: with Windows Terminal the chain ends at explorer.exe.

Spinner word: while working, read the console's visible rows (`CONOUT$`,
`ReadConsoleOutputCharacterW`) for Claude Code's status line
(`✽ Germinating… (16s · …)`) and show that word on the island.
