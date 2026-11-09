# AGENTS.md

Guidelines for AI agents and contributors working on Sub.NETUI, a Terminal.Gui v2 TUI client for Navidrome (Subsonic API) that plays audio through `mpv`.

## Build and verify

```sh
dotnet build Sub.NETUI.slnx                                   # must finish with 0 warnings
dotnet publish Sub.NETUI/Sub.NETUI.csproj -c Release -r linux-x64   # Native AOT, must have no trimming/AOT warnings
git diff --check
```

There is no automated test project yet. The TUI cannot be checked interactively in a headless shell. Run the published binary under `script -qec '...' /dev/null` with the `NAVIDROME_URL`, `NAVIDROME_USER`, and `NAVIDROME_PASSWORD` environment variables for smoke tests.

## Project layout

- `Sub.NETUI/Configuration`: settings loading/saving (env vars override `~/.config/subnetui/config.json`).
- `Sub.NETUI/Subsonic`: models, `SubsonicClient`, `SubsonicJsonContext`, API errors.
- `Sub.NETUI/Playback`: `IAudioPlayer` / `MpvAudioPlayer` (mpv over IPC), `PlaybackQueue`, `IMediaSession` / `MprisMediaSession` / `NullMediaSession`, `IPlaybackService`, implemented by `PlaybackController` (UI-agnostic playback logic, runs in the daemon or in-process as a fallback) and `DaemonPlaybackClient`.
- `Sub.NETUI/Daemon`: `subnetui daemon` mode (`PlaybackDaemon`, Unix socket server), `DaemonPlaybackClient` (TUI side, starts the daemon if needed), `ProcessDetacher` (`setsid` + stdio to `/dev/null`), `DaemonControl` (`subnetui daemon stop`), and `DaemonProtocol` (JSON-lines commands/events, `DaemonJsonContext`).
- `Sub.NETUI/UI`: `LoginWindow`, `MainWindow` (coordinator), reusable panes (`LibraryListPane<T>` and subclasses), `NowPlayingPane`, `StatusBar`.
- `Sub.NETUI/Program.cs`: composition root (Pure DI, no container).

## Rules

- **Native AOT is required.** Do not use reflection-based serialization or libraries that need it. Register every new JSON root type in the matching source-generated `JsonSerializerContext`.
- **Keep the layers separate.** Views never call HTTP or `mpv` directly; `MainWindow` coordinates.
- **UI thread.** Await network/player/D-Bus work off the UI thread and apply results with `app.Invoke(...)`. Never block the main loop.
- **D-Bus threads.** `Tmds.DBus.Protocol` runs `await` continuations of call replies inline on its receive loop. Never block after awaiting a D-Bus call without first leaving that thread (`MprisMediaSession.CreateAsync` ends with `await Task.Yield()` so `app.Run` does not stall incoming calls).
- **Daemon protocol.** Daemon clients may write a command and close at once, so never let a failed write to a client discard commands it already sent. Keep `DaemonCommand`/`DaemonEvent` changes backward compatible or tell users to run `subnetui daemon stop` after upgrading.
- **Do not write to stdout/stderr** while the TUI is running; it corrupts the terminal.
- **Terminal.Gui focus.** Container `SuperView`s need `CanFocus = true`. Global shortcuts go through `app.Keyboard.KeyDown`. Playback shortcuts use `Ctrl` so plain letters stay free for list search.
- **Cancellation.** Selection-driven loads use a `CancellationToken` and ignore stale results.
- **Language.** All code, UI text, comments, and docs are in English.
- **Style.** Follow the existing code style: file-scoped namespaces, `sealed` classes, `_camelCase` fields, few comments.
- Update `README.md` when behavior or shortcuts change.

## Open work

- Windows (SMTC) and macOS (Now Playing) media sessions are not implemented; they should implement `IMediaSession`.
