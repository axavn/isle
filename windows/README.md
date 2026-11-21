# Isle for Windows

A Windows (WPF, .NET 10) port of Isle's island: the same collapsed pill and open panel
sizes, notch outline, spring motion, hover zones, album-art palette and equalizer,
Claude Code dot-matrix marker, and Music / Claude faces. Agenda, Pomodoro, Shelf and
the extra backgrounds are not ported yet.

- Music: any player that publishes to Windows media controls (Spotify, browsers, …);
  the equalizer reads system audio through WASAPI loopback.
- Claude Code: session state from hooks; approve / deny / answer from the island;
  replies are typed straight into the session's console (see `docs/PROTOCOL.md`).

```
dotnet build IsleWindows.sln
dotnet run --project src/IsleWin
```

Design, layout and motion are from Isle by Matthew Hamilton (MIT, see `../LICENSE`).
Fonts are Segoe UI Variable in place of SF Pro; icons are redrawn to match SF Symbols.
