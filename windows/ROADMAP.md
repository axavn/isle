# Isle for Windows: Roadmap

Compared with Isle for macOS v1.3.2. Grouped by what you would notice first.

## Done

- Look and motion: sizes, notch, springs, taper, hover zones, proximity swell, palette, fonts, icons.
- Faces: Home, Music, Claude. Slide between them. Home and Music use the 146 pt panel; Claude grows to 230.
- Music: art, title, marquee, transport, playbar, shuffle, repeat, cover colours, six-bar equalizer.
- Equalizer taps only the player's process via process loopback (Spotify by default). It follows the SMTC
  source and rebuilds on relaunch or after a 5 s stall.
- Claude: states, dot-matrix marker, spinner word from the terminal, Approve / Deny, done line, subagent
  line, usage-limit message, pin a session, Jump, reply from the island.
- Claude desktop-app sessions are shown (no Reply box; Jump raises the desktop window).
- `AskUserQuestion`: single-select, multi-select (checkboxes, Next / Done), several questions ("1 of N"),
  option descriptions, "Type something" free text.
- Hook writes retry on "access denied"; readers share the file.
- Placement: top of screen, floating (hold to detach, carry, dock), taskbar mode.
- Taskbar: icons read about 10 times a second off the UI thread. The pill shrinks to the free stretch
  beside the weather, moves to the other side of the icons if needed, and hides only if neither fits.
- Taskbar: hides only for a real full-screen app (also needs Windows' own full-screen signal).
- Mouse back / forward buttons and Alt+Left / Alt+Right step faces. A quick sideways drag pages.
- Hover-grow of controls (his 1.3.2): Claude buttons, Home tiles, music keys, home mark.
- Home mark restyled like his NotchCrumb.
- Faces are a list in his order (Home, Music, Claude, Pomodoro, Shelf); Home lays out one tile per face.
- Pomodoro face, Home glance and pill seat (opt-in from the tray). Not live-verified.
- Shelf (references only, 10 max, `~/.isle/shelf.json`) and the file-drop face: Keep on shelf, Start Claude
  here. Not live-verified.
- Toasts on the pill (4 s, queue of 2, shortcut slot) and sounds (his four files; Claude alert / done off,
  Pomodoro on). Not live-verified.
- Plan approval (allow + `updatedInput`), "Always…" with `updatedPermissions`, deny with a typed reason,
  his approval card and "Plan ready" card. Not live-verified.
- Claude shortcuts: Ctrl+' approve, Ctrl+Shift+' deny (Windows keeps Ctrl+Esc), Alt+1–4 options.
- 93 tests passing.

## Next

Biggest first.

- **Live checks** of session 3 and what else is untested (see `HANDOFF.md`).
- **Settings window.** The store (`IsleSettings`, `~/.isle/settings.json`) exists; the tray's Pomodoro and
  Sounds items stand in for the window. Pomodoro lengths and sound choices have no UI yet.
- **Agenda face** (calendar and reminders; would need Windows' calendar APIs or Outlook/Google).
- **Toast sources:** power, Bluetooth device battery, session done, auto-mode blocked.
- **Track-change crossfade** (his 1.3.1).

## Later

Things you would notice, but smaller or harder.

Music
- Output device picker, output volume and mute.
- Waveform source Live / Animated / Off. Show or hide scrubber and shuffle/repeat.

Claude
- Guarded commands (`guard` verb on PreToolUse), auto-mode block line (`PermissionDenied`).
- Keep awake while a turn is working.
- Edit / diff preview on approvals. Friendly activity line ("Editing App.swift").
- Step trail under the working line. "Left running" leftovers after done.
- Background sessions (`claude agents --json`). Live usage-limit countdown.
- Shortcut hints on the approval buttons.
- Accent colour for Claude-only mode. Click-out releases the text field.
- Mode picker: Music / Claude / Both.

Island
- Tile-to-face morph. Reduce-motion respect.
- Per-display position. Island on every display.
- Remember last face.
- Power and Bluetooth battery toasts.
- Glass, Prism and Bleed styles. Marker Editor.

App
- Launch at login. First-run onboarding.
- Auto-update with signed manifest. An installer (MSIX, zip or winget).
- Hook helper version stamp and refresh on launch.
- Full tray menu (Settings, Check for Updates).

## Not applicable on Windows

- Haptics (no trackpad haptic API).
- AirDrop on the drop face (no public API for Nearby Sharing).
- Animation Gallery (debug tool).
- Notch size metrics. The top-of-screen island uses a fixed 140 pt gap.
- macOS permission prompts in onboarding.
- Lyrics. Removed from macOS too.

## Windows-only

- Taskbar mode.
- Reply by `WriteConsoleInput`, and the spinner word read from the console.
- Any SMTC player, not just Spotify and Apple Music.
- Spotify on other devices through the Web API (opt-in, bring your own Client ID): "Playing on <device>"
  above the title, controls via the API, idle equalizer animation. Done 2026-11-25; not verified live.
