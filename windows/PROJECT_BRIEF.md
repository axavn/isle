# Isle for Windows: Project Brief

## 1. Goal

A faithful Windows copy of Matthew Hamilton's macOS Isle (MIT): a Dynamic Island for Spotify and Claude Code.
Same look, same motion, same faces. Windows-only additions are allowed where Windows needs them, such as
taskbar mode.

Stack: C# / .NET 10, WPF, Windows 10 2004+ / Windows 11.

## 2. Principles

- **Match his look and motion.** Sizes, notch shape, springs, taper, hover zones, palette, fonts and icons
  are ported with his constants. When unsure, read his Swift file and copy the number.
- **Keep all Windows work under `windows/`.** His macOS app on `main` stays untouched. `main` tracks his
  upstream. `windows-port` is upstream plus `windows/`.
- **Hooks never block Claude Code.** The helper always exits 0. If the app is not running, Claude Code
  shows its own prompt.
- **Don't invent features.** Port what he has. Say so in `ROADMAP.md` when we skip something.
- Keep code compact. Comment only where the reason isn't obvious.

## 3. Architecture

| Project | Kind | Job |
|---|---|---|
| `src/IsleWin` | WPF app | The overlay window, tray icon, music (SMTC, audio), views, placement. |
| `src/IsleWin.Claude` | WPF class library | The Claude face: view model, view, dot matrix, terminal bridge, hover-grow. |
| `src/IsleWin.Core` | net10.0 library | Pure logic, no UI: status store, requests, hook installer, `HookHandler`, text helpers. |
| `src/IsleCli` | net10.0 console, `isle-cli` | The hook helper. A thin wrapper over `HookHandler`. Also `install`. |
| `tests/IsleWin.Tests` | xUnit | Tests for Core and the question logic. |

Data flow: Claude Code runs `isle-cli <verb>` as a hook. The helper writes session and request files under
`~/.isle/`. The app watches those files. Answers go back as response files that the helper prints to Claude
Code. See `docs/PROTOCOL.md`.

## 4. Code map

| File | What it does |
|---|---|
| `src/IsleWin/IslandWindow.xaml(.cs)` | The window and the island. A fixed 600x300 transparent window; the island is drawn inside it. Springs, the notch outline, hover zones, collapsed pill layout, open/close, `RequestKeyboard`. |
| `src/IsleWin/IslandWindow.Gestures.cs` | Pager (a face list in his order: Home, Music, Claude, Pomodoro, Shelf; swipe, Shift+wheel, mouse buttons, drag). Carry: press and hold 0.35 s to detach, floating capsule, dock outline at the top, `KeepOnScreen`, placement save/load. |
| `src/IsleWin/IslandWindow.Taskbar.cs` | Taskbar mode: the pill sits beside the Widgets/weather button (found with UI Automation). A panel pops up above it. Handles auto-hide, full screen, staying topmost, and a taskbar outline while carrying. |
| `src/IsleWin/IslandWindow.Toasts.cs` | Toasts on the collapsed pill: 4 s, same kind replaces, queue of 2, a 2.5 s slot for shortcut notices. |
| `src/IsleWin/IslandWindow.Shelf.cs` | File drag onto the island: the drop face (Keep on shelf, Start Claude here), the Shelf face and its Home tile. |
| `src/IsleWin/IslandWindow.ClaudeKeys.cs` | Claude shortcuts (held only while a request waits) and the Claude alert / done sounds. |
| `src/IsleWin/IslandMotion.cs` | `Spring` (SwiftUI response/damping), his motion constants, `Outline()` notch geometry, `Taper`. |
| `src/IsleWin/Views/` | `MusicView`, `HomeView` + `Crumb`, `Equalizer` (his 6-bar), `Marquee`, `PomodoroService/View/Ring/Pill`, `ShelfView`, `DropFaceView`. `src/IsleWin/Shelf/`: shell thumbnails, Start Claude. |
| `src/IsleWin/Media/` | `MediaService` (SMTC), `WaveformService` (WASAPI), `ProcessLoopback.cs` (audio of one process only), `SpotifyWeb.cs` (Web API: PKCE sign-in, token in the Windows credential vault, polling, controls), `CoverColors` (his OKLab palette), `Sounds.cs` (his four sounds, NAudio). |
| `src/IsleWin.Claude/` | `ClaudeViewModel`, `ClaudeView.xaml` (his Claude face), `DotMatrix` (his 5x5 marker), `TerminalBridge` (console attach / inject / spinner word / desktop window), `IsleStyle` (fonts, icons). |
| `src/IsleWin.Core/SpotifyApi.cs` | Web API parsing, PKCE and token helpers (tested). `src/IsleWin/SpotifyConnectWindow.cs` is the tray's Connect Spotify window. |
| `src/IsleWin.Claude/HoverGrow.cs` | The `HoverGrow` attached property: controls grow on hover (his 1.3.2). |
| `src/IsleWin.Core/` | `Api.cs` (records, `IIslandHost`). `Impl.cs` (status store, registry, requests, hook installer, transcript reader, `HookHandler`). `ClaudeText.cs` (done line, usage limit, subagent line). `ClaudeApproval.cs` (approval card, plan outline, Always review). `Settings.cs` (`IsleSettings`). `Pomodoro.cs` (timer state machine). `Shelf.cs` (`ShelfStore`). |
| `docs/PROTOCOL.md` | Hook / request protocol and how replies and Jump work. |

## 5. Working rules

- Build and tests first. Keep screenshots to a minimum.
- Plan in chat before bigger features.
- Update `HANDOFF.md` at the end of every session.
