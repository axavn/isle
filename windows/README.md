# Isle for Windows

**A Dynamic Island for the Windows desktop, for Spotify and Claude Code.**

A WPF / .NET 10 port of [Isle](https://github.com/matthewhamilton3141/isle) by Matthew Hamilton, the macOS
Dynamic Island app. The look, the motion and the two faces come from his app. This port lives in the
`windows/` folder of the fork, on the `windows-port` branch.

Isle is MIT licensed. Copyright (c) 2026 Matthew Hamilton. His copyright notice stays in the repository.

---

## What it does

The island is a small black shape that sits on your screen. It shows what is playing and what Claude Code is
doing. It expands on hover and folds away when you leave.

### Music face

- Now playing: art, title, artist, scrolling title.
- Play, pause, next, previous, shuffle, repeat, and a scrubbable playbar.
- A six-bar equalizer that reacts to the audio. It taps only the player's own process (Spotify by default).
- Colours taken from the cover art.
- Works with Spotify and any other player that uses Windows media controls (SMTC).

### Claude Code face

- Shows what each session is doing: working, needs approval, has a question, done, failed.
- Opens on its own when Claude needs you.
- Approve or Deny a permission request from the island.
- Answer `AskUserQuestion` from the island: single-select, multi-select, several questions in a row,
  and "Type something" for free text.
- Reply to a session by typing in the island (CLI sessions).
- Jump to the session's terminal window, or to the Claude desktop app.
- A done line, a subagent line, and a usage-limit message.
- Sessions started by the Claude desktop app are shown too.

### Placement

- **Taskbar:** a pill the size of the search box, beside the weather button. A panel pops up above it.
- **Top of screen:** top centre, like the notch.
- **Floating:** press and hold to pick it up, carry it anywhere, drop it. Drag it to the top to dock it again.

### Gestures

- Swipe sideways, Shift+wheel, or click and drag sideways to page between faces.
- Mouse back and forward buttons, or Alt+Left and Alt+Right (only while the island is open).
- Press and hold for 0.35 s to detach the island.
- Controls grow a little when you hover them.

---

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11.
- .NET 10 SDK to build. The .NET 10 desktop runtime to run.
- Claude Code, if you want the Claude face.

## Build, run, test

```sh
taskkill //IM IsleWin.exe //F            # a running app locks the DLLs
dotnet build IsleWindows.sln -v q -nologo
dotnet test tests/IsleWin.Tests
(cd src/IsleWin/bin/Debug/net10.0-windows10.0.19041.0 && ./IsleWin.exe)
```

## Install the Claude Code hooks

The Claude face needs hooks in Claude Code. Run:

```sh
src/IsleCli/bin/Debug/net10.0/isle-cli.exe install
```

This copies `isle-cli.exe` to `~/.isle/bin` and merges the hooks into `~/.claude/settings.json`. A backup is
kept. You can also use the tray icon menu: install or remove the hooks there.

The hook helper never blocks Claude Code. If Isle is not running, Claude Code shows its own prompt.

## Where things live

- `~/.isle/sessions/`: one status file per Claude session.
- `~/.isle/requests/`: pending approvals and questions, and their responses.
- `~/.isle/island.json`: placement (taskbar, top, floating).
- `~/.isle/islewin.log` and `~/.isle/isle-cli.log`: logs.

The tray icon menu has **Place on taskbar**, **Place at top of screen**, install and remove hooks, and quit.

The file protocol is in [docs/PROTOCOL.md](docs/PROTOCOL.md).

---

## What's different from macOS

- **Taskbar mode** is new. macOS has no taskbar.
- **No notch.** The top-of-screen island has a fixed 140 pt gap in the middle.
- **Audio** uses Windows process loopback, not a system-wide tap.
- **Players** come from SMTC, not AppleScript, so more players work.
- **Replies** are typed into the session's console with `WriteConsoleInput`. No clipboard, no focus change.
- **Not ported yet:** the Settings window, Pomodoro, Agenda, Shelf, Glass and Prism styles, toasts and sounds,
  output-device picker, updater. See [ROADMAP.md](ROADMAP.md).
