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
- 41 tests passing.

## Next

Biggest first.

- **Settings window and store.** Most missing toggles hang on it. Today only `island.json` exists.
- **Pomodoro, Shelf and Agenda faces.** Home has two tiles only.
- **Toasts and sounds.** A small pill message after Approve / Deny / Answer. Alert and done sounds.
- **Plan approval** (ExitPlanMode as "Plan ready"), **"Always allow"** with `updatedPermissions`, **deny with
  a typed reason.**
- **Track-change crossfade** (his 1.3.1).
- **Live checks** of what is untested (see `HANDOFF.md`).

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
- Keyboard shortcuts for approve, deny and options.
- Accent colour for Claude-only mode. Click-out releases the text field.
- Mode picker: Music / Claude / Both.

Island
- Tile-to-face morph. Reduce-motion respect.
- Per-display position. Island on every display.
- Remember last face.
- Power and Bluetooth battery toasts.
- Glass, Prism and Bleed styles. Marker Editor.
- File drop onto the island (Start Claude here, Keep on shelf).

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
