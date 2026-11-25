# Where we left off

_Last updated: 2026-11-25 (end of session 2)_

This file replaces `SESSION.md` as the session handoff. Read it at the start of every session.

## What this is

**Isle for Windows** is a WPF / .NET 10 port of Matthew Hamilton's macOS Isle (a Dynamic Island).
The design source is the user's fork: https://github.com/axavn/isle. His code is on `main` (MIT, keep his
copyright notice). Our port lives on the **`windows-port`** branch, under `windows/`.

There are three faces: Home, Music and Claude.
- **Music:** Spotify and other players through Windows media controls (SMTC). The equalizer taps only the
  player's process (Windows process loopback; Spotify by default).
- **Claude Code:** session state from hooks; Approve/Deny, answer questions, and reply from the island.
  Replies are typed into the session's console with `WriteConsoleInput`. Desktop-app sessions show too.

Docs: `README.md`, `PROJECT_BRIEF.md`, `ROADMAP.md`, `docs/PROTOCOL.md`.

## Build / run / test

```sh
taskkill //IM IsleWin.exe //F            # the running app locks the DLLs
dotnet build IsleWindows.sln -v q -nologo
dotnet test tests/IsleWin.Tests          # 41 tests, all passing
(cd src/IsleWin/bin/Debug/net10.0-windows10.0.19041.0 && ./IsleWin.exe >/dev/null 2>&1 &)
```

- Hooks: `src/IsleCli/bin/Debug/net10.0/isle-cli.exe install`. It copies itself to `~/.isle/bin` and
  merges hooks into `~/.claude/settings.json`.
- State lives in:
  - `~/.isle/sessions/`, `~/.isle/requests/`
  - `~/.isle/island.json` (placement: top / floating / taskbar)
  - Logs: `~/.isle/islewin.log`, `~/.isle/isle-cli.log`
- Tray icon menu: **Place on taskbar** / **Place at top of screen**, install/remove hooks, quit.

## Pushing to the fork

The local folder is **not** a git repo. The fork's `main` now tracks Matthew's upstream (fast-forwarded to
1.3.2). `windows-port` is upstream main plus `windows/`. To push:
1. Clone the fork to a temp folder: `git clone https://github.com/axavn/isle.git`, then `git checkout windows-port`.
2. Copy `src tests docs IsleWindows.sln .gitignore` and the root docs (`README.md`, `PROJECT_BRIEF.md`,
   `ROADMAP.md`, `HANDOFF.md`, `CLAUDE.md`, `AGENTS.md`) into its `windows/` folder, then delete every `bin/`, `obj/` and
   `.vs/` folder. Don't touch his root files.
3. Commit (end the message with the Co-Authored-By line) and `git push`.

Only push when the user asks. Nothing from session 2 is pushed yet.

## Code map

See `PROJECT_BRIEF.md`, section 4. New this session: `src/IsleWin/Media/ProcessLoopback.cs` and
`src/IsleWin.Claude/HoverGrow.cs`.

## Done

Session 1 (all on `windows-port` unless noted):
- Full port of his look and motion: sizes, notch, springs, taper, hover zones, proximity swell, palette,
  equalizer, dot matrix, faces, fonts and icons.
- Collapsed pill has a 140 pt centre gap; padding clears the corner flare.
- Slide between faces, detach/carry/dock, click-out, done summary, subagent count, usage-limit message.
- Drops clamped to the work area (`KeepOnScreen`). Taskbar mode. Mouse capture during a carry.

Session 2 (2026-11-25, not pushed):
- **Taskbar, speed:** the icon row (Start, Search, Task View, apps) is read about 10 times a second off the
  UI thread with one cached UI Automation call (was every 5 s), so the pill reacts at once when apps open.
- **Taskbar, fit:** the pill shrinks to the free stretch beside the weather, moves to the stretch on the
  icons' other side (left of the tray) if that one is too small, and hides only when neither holds its
  content. It used to stop at 120 px and cover icons.
- **Taskbar, vanishing:** the island vanished when Search was clicked (and at random) because any
  screen-sized foreground window (Search, Start, Alt+Tab, Task View) counted as a full-screen app. Now it
  also needs Windows' own full-screen signal (`SHQueryUserNotificationState`: busy, D3D, presentation).
  And when the taskbar can't be found for a moment (explorer restarting) the island hides and waits; it
  used to jump to the top of the screen and save that as its place.
- **Top of screen:** content was misplaced at the top, or after switching from the taskbar, because taskbar
  mode moved the panel content down and top mode never moved it back (`Canvas.SetTop(ExpandedRoot, 0)`).
- **Faces:** Home and Music keep the standard 146 pt panel like Isle (only Claude grows, to 230). The home
  mark is restyled like his NotchCrumb (grid 0.55, chevron 0.35) and grows on hover.
- **Mouse:** back/forward side buttons and Alt+Left/Right (registered only while open) step faces. A quick
  sideways click-and-drag pages. Press-and-hold still detaches. Shift+wheel still pages.
- **Hover-grow** (his 1.3.2): `HoverGrow` attached property (2 pt a side, max 1.15x, 0.22 s spring) on
  Claude buttons and options, Home tiles, music keys, home mark.
- **Claude questions:** `AskUserQuestion` shows the question instead of Allow/Deny. Single-select click,
  multi-select checkboxes with Next/Done, several questions with "1 of N", option descriptions on one line,
  "Type something" free text.
- **Claude desktop-app sessions** are shown. Hook writes retry on "access denied" (seen 6 times in the log),
  and readers share the file.
- **Music:** the equalizer taps only the player's process via Windows process loopback (Spotify by default;
  re-aimed at the SMTC source; rebuilt on relaunch or a 5 s stall), not all system audio.
- **Tests:** 41 passing (8 new in `QuestionTests.cs`).
- **Docs:** README, PROJECT_BRIEF, ROADMAP, this file, and an update to `docs/PROTOCOL.md`.

## Open / next

- Not verified live:
  - Process loopback with music actually playing.
  - Desktop-window Jump.
  - Taskbar shrink / move / hide with many apps.
  - Full-screen hide with a real full-screen app.
  - Alt+Left/Right and drag paging.
  - The question UI with real `AskUserQuestion` calls.
  - Question panel height for 4 options with descriptions: it may clip at 230. Isle's `ClaudePanelLayout`
    fitting is not ported.
  - From session 1: a real touchpad swipe (tune `SwipeGain`), hover-open on a floating pill, taskbar
    auto-hide, left-aligned icons, top taskbar, reply injection, the done / subagent / limit lines.
- **Next, in order:** Settings window + store (biggest gap), Pomodoro / Shelf / Agenda faces, toasts +
  sounds, plan approval / "Always allow" / deny with reason, track-change crossfade, then live-verify the
  list above. Full gap list in `ROADMAP.md`.
- Possible: a longer hold time (0.35 s to 0.6 s) if accidental pick-ups happen.
- Ideas offered, not started: step trail; iPad audio (show the equalizer whenever audio plays without SMTC
  track info, or a mic-input mode).

## User preferences

- They want a plan in chat first for bigger features ("tell me if it's possible and how before executing").
- Keep screenshots to a minimum to save usage, and prefer build + tests.
- Use Edit, not Python/sed heredocs, for C# lines with backslashes (escaping broke builds several times).
- **Update this file at the end of every session**, or after any big change.
