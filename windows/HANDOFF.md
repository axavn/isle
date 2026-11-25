# Where we left off

_Last updated: 2026-11-25 (session 3: Pomodoro, Shelf, toasts, sounds, Claude approval extras)_

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

Only push when the user asks. Session 2 is pushed (`windows-port` at adb6d32; `main` at 7c9c8cf = his 1.3.2).
The Spotify Web API work and all of session 3 are local only until the next push.

## Code map

See `PROJECT_BRIEF.md`, section 4. New in session 2: `src/IsleWin/Media/ProcessLoopback.cs`,
`src/IsleWin.Claude/HoverGrow.cs`. New in session 3:
- Core: `Settings.cs` (`IsleSettings`, `~/.isle/settings.json`), `Pomodoro.cs` (state machine), `Shelf.cs`
  (`ShelfStore`, `~/.isle/shelf.json`), `ClaudeApproval.cs` (card wording, plan outline, "Always" review).
- Window partials: `IslandWindow.Toasts.cs`, `IslandWindow.Shelf.cs` (drop handling, Shelf face),
  `IslandWindow.ClaudeKeys.cs` (Claude shortcuts, sounds). The pager in `IslandWindow.Gestures.cs` is now a
  face list (`_order`, `SetFaceAvailable`), and `HomeView.ShowFaces`/`AddTile` lay out any number of tiles.
- Views: `PomodoroService/View/Ring/Pill.cs`, `ShelfView.cs`, `DropFaceView.cs`; `Shelf/ShellThumbnails.cs`,
  `Shelf/StartClaude.cs`; `Media/Sounds.cs` plus `Sounds/*.m4a` (his four files, copied to the output).
- Claude: `FitText.cs`; the approval card, "Always…", deny-with-reason and plan card in `ClaudeView.xaml`.

## Done

Session 1 (all on `windows-port` unless noted):
- Full port of his look and motion: sizes, notch, springs, taper, hover zones, proximity swell, palette,
  equalizer, dot matrix, faces, fonts and icons.
- Collapsed pill has a 140 pt centre gap; padding clears the corner flare.
- Slide between faces, detach/carry/dock, click-out, done summary, subagent count, usage-limit message.
- Drops clamped to the work area (`KeepOnScreen`). Taskbar mode. Mouse capture during a carry.

Session 2 (2026-11-25, pushed):
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
- **Spotify on other devices** (after the push; local only): the Web API, opt-in from the tray's
  **Connect Spotify…** (each user's own Client ID; Spotify caps dev-mode apps at owner + 5 and needs Premium to
  own one). Used only when this PC's Spotify isn't playing (polling is suspended while it is): every 2 s while
  playing elsewhere (1 s while open), 10 s otherwise. Shows "Playing on <device>" above the title at the
  artist's size (it rises into the band, so the panel stays 146), the equalizer's idle animation (empty tap
  target), and routes the controls to the API. Refresh token in the Windows credential vault ("Isle.Spotify"),
  client id in `~/.isle/spotify.json`. Redirect URI `http://127.0.0.1:43827/callback`.
- **Tests:** 55 passing (8 in `QuestionTests.cs`, 14 in `SpotifyApiTests.cs`).
- **Docs:** README, PROJECT_BRIEF, ROADMAP, this file, and an update to `docs/PROTOCOL.md`.

Session 3 (2026-11-25, local only; built by three agents in isolated copies, merged and wired by hand):
- **Faces are a list** in his `availableTabs` order: Home, Music, Claude, Pomodoro (when on), Shelf (while
  it holds something). Swipe, Alt+arrows, side buttons and the Home row all walk it; a face that goes while
  open falls back to Home.
- **Pomodoro** (his `PomodoroTimer`): 25/5/15, long break after 4, a break starts itself after a focus, the
  next focus waits paused. Face with a 114 pt ring, Home tile glance ("Focus · 2 of 4"), and a seat on the
  pill from first start until reset (22 pt ring + clock alone; 16 pt ring + clock after Claude or opposite
  music; the clock slot is sized from a fixed template so it never jitters). Opt-in from the tray's
  **Pomodoro** item, as his is off by default. Not persisted (his isn't either).
- **Shelf:** references only (the file is never copied or moved), 10 at most, oldest first, de-duplicated,
  saved to `~/.isle/shelf.json`, pruned when the panel opens, and a FileSystemWatcher drops moved or deleted
  files. 1 row of 5 (56 pt thumbnails, panel 186) or 2 rows (44 pt, panel 226). Shell thumbnails via
  `IShellItemImageFactory`. Drag out is Copy or Link, never Move, and the item stays.
- **Drop face:** the island reacts when a file drag reaches it (no global mouse hook), opens on the drop
  face at 146 with **Keep on shelf** (then shows the Shelf face) and **Start Claude here** (one folder only;
  `wt.exe -w new -d <folder> cmd /k claude`, else `cmd /k claude`). No AirDrop/Share target. Ignored while
  a Claude alert is up and for drags out of the shelf.
- **Toasts** (his `IslandToast`): borrow the collapsed pill for 4 s, same kind replaces, queue of 2, hidden
  by a live Claude alert, 0.25 s cross-fade. Kind `shortcut` has its own 2.5 s slot shown even over an alert.
  Only shortcut decisions produce toasts so far (no battery / calendar sources on Windows yet).
- **Sounds:** his four `.m4a` files via NAudio (restart, never layer). Claude alert and Claude done (off by
  default), Pomodoro interval end (on). Tray **Sounds** submenu; switching one on plays it.
- **Claude approval extras:** plan approval answers `allow` + `updatedInput` (it used to fail); his deny
  wording plus " and said: <words>"; his 2-line mono card; **Always…** with a Confirm/Back review that sends
  the suggestions verbatim as `updatedPermissions`; deny with a typed reason (bubble pill → field); "Plan
  ready" card.
- **Claude shortcuts** (held only while a request waits, 0.5 s after it appears, not while a field is
  typing): Approve **Ctrl+'**, Deny **Ctrl+Shift+'** (his Ctrl+Esc, but Windows holds that for Start: error
  1409; the code tries Ctrl+Esc first), options **Alt+1–4**, Approve = Next/Done on multi-select. A press
  while the Claude face isn't in view only reveals it (Deny always denies); the review needs 0.5 s in view.
- **Tests:** 93 passing (12 Pomodoro, 8 Shelf, 18 approval). The test project now targets Windows + WPF.

## Open / next

- Not verified live (session 3: only "starts with no errors" was checked):
  - Pomodoro face, ring and pill layouts, the interval-end sound.
  - Shelf face, thumbnails, drag out; the drop face and both targets; WPF drag-leave handling (a leave
    with no enter behind it ends the drop). Drags from an elevated app can't reach Isle (UIPI).
  - Toast fade and widths on the pill and the taskbar capsule.
  - The approval card, Always… review, deny field, plan card; the shortcuts and their toasts.
  - Claude alert/done sounds.
- Not verified live (earlier):
  - Spotify Web API: sign-in, polling, remote controls, the device line, this-PC detection (by machine name).
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
- **Next, in order:** live-verify session 3; a Settings window (the tray items stand in for it; the store,
  `IsleSettings`, exists now: Pomodoro lengths and sound choices are in `~/.isle/settings.json` but have
  no UI); Agenda face; toast sources (battery, Bluetooth devices, session done); shortcut hints on the
  buttons; track-change crossfade. Full gap list in `ROADMAP.md`.
- Known small deviations: Fluent glyphs for his SF Symbols; no 0.2 s crossfades on Pomodoro text or the
  shelf fan; shelf thumbnails don't refresh when a file's contents change; the approval card's stroke is
  fixed amber; no guarded-command chip.
- Possible: a longer hold time (0.35 s to 0.6 s) if accidental pick-ups happen.
- Ideas offered, not started: step trail; iPad audio (show the equalizer whenever audio plays without SMTC
  track info, or a mic-input mode).

## User preferences

- They want a plan in chat first for bigger features ("tell me if it's possible and how before executing").
- Keep screenshots to a minimum to save usage, and prefer build + tests.
- Use Edit, not Python/sed heredocs, for C# lines with backslashes (escaping broke builds several times).
- **Update this file at the end of every session**, or after any big change.
