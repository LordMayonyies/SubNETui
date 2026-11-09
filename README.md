# Sub.NETUI

Sub.NETUI is a terminal UI for browsing a Navidrome library through its Subsonic-compatible API and playing music with `mpv`.

## Requirements and configuration

- .NET 10 SDK to build the application.
- An HTTP/HTTPS-accessible Navidrome instance and its credentials.
- `mpv` installed and available on `PATH` for audio playback.
- On Linux, a D-Bus session bus for desktop media controls.

Configuration is read from `~/.config/subnetui/config.json` and can be overridden with the `NAVIDROME_URL`, `NAVIDROME_USER`, and `NAVIDROME_PASSWORD` environment variables:

```json
{
  "Url": "https://navidrome.example.com",
  "Username": "user",
  "Password": "password"
}
```

If configuration is missing or invalid, a sign-in window opens so you can enter or correct the server URL and credentials. The application checks the Navidrome connection before saving the settings to `~/.config/subnetui/config.json`; on Unix, the file is accessible only to the current user. URLs with a base path, such as `https://server/music`, are also supported.

## Running and publishing

```sh
dotnet run --project Sub.NETUI
dotnet build Sub.NETUI.slnx
dotnet publish Sub.NETUI/Sub.NETUI.csproj -c Release -r linux-x64
```

### Background playback

Playback runs in a background daemon, so music keeps playing after you quit the TUI or close its terminal. The TUI connects to the daemon on startup, starting it if needed (detached into its own session with stdio on `/dev/null`), and shows the current track when you reopen it. If the daemon cannot be reached, the TUI falls back to in-process playback and says so in the window title.

`subnetui daemon` runs playback headless: `mpv`, the queue, and the MPRIS session, with no terminal UI. It listens on `$XDG_RUNTIME_DIR/subnetui.sock` (user-only permissions), records its PID in `subnetui.pid`, and refuses to start if another daemon holds `subnetui.lock`.

Ways to shut the daemon down:

- `Ctrl+X` in the TUI.
- `subnetui daemon stop` from a shell. It waits until the process has exited, and kills it (together with `mpv`) if it does not respond within five seconds. Run it after installing a new build so the next TUI starts the new daemon.
- The desktop player's quit action (MPRIS `Quit`).
- `SIGTERM` or `SIGINT`.
- Automatically when no TUI is connected: five minutes after playback stops, or thirty minutes after it is paused. The play queue is saved on Navidrome first, so nothing is lost.

The protocol is one JSON object per line. Clients send commands such as `{"type":"play-queue","tracks":[...],"index":0}`, `{"type":"toggle-pause"}`, or `{"type":"seek","position":42.5}`; the daemon sends `state`, `progress`, and `notice` events, starting with the current state when a client connects. `subnetui daemon --detach` is what the TUI uses to start it.

### Navidrome sync

Playback is reported to Navidrome (`Playback/NavidromeSessionSync.cs`), so other clients and the web UI see what Sub.NETUI plays:

- **Now playing**: every time a track starts or resumes, Sub.NETUI calls `scrobble` with `submission=false`, and Navidrome shows the track under the `SubNETUI` client.
- **Scrobbles**: after half the track or four minutes of actual listening (whichever comes first; seeking does not count), it calls `scrobble` with `submission=true`. This updates play counts and is forwarded to Last.fm or ListenBrainz if they are linked in Navidrome.
- **Resume**: the queue, current track, and position are saved with `savePlayQueue` when the track changes, on pause and stop, every 30 seconds while playing, and on shutdown (including `SIGTERM` when the computer shuts down). On startup the daemon loads them with `getPlayQueue` and restores the track **paused**; `Ctrl+Space` or the media keys resume from the saved position. Queues saved by other clients that support play queues are restored too. Stopping resets the saved position to the start of the track, and finishing the album clears the saved queue.

Server errors are ignored so they never interrupt playback.

Native AOT creates a native executable for the runtime specified with `-r`; choose the runtime identifier for your OS and architecture. Configuration and Subsonic responses use `System.Text.Json` source generation to avoid dynamic reflection during publishing. `mpv` is an external process and is not included in the executable.

## Usage

- Arrow keys: navigate artists, albums, and tracks; focus stays in the list at its beginning or end.
- `Tab`: switch between the artists, albums, and tracks panes.
- `Enter` on an artist or album: move to the next pane; `Esc`: return to the previous pane. Arrow keys never switch panes.
- `Enter` on a track: play it and continue with the following tracks on the album.
- Letters: jump to the first artist, album, or track whose name starts with that letter (`ListView` incremental search).
- Digits in the tracks pane: select a track by its track number; type consecutive digits (for example, `1` then `2` for track 12) within one second.
- `Ctrl+Space`: pause or resume; `Ctrl+S`: stop.
- `Ctrl+N` / `Ctrl+P`: next / previous track (previous restarts the current track if more than 3 seconds have played).
- `Ctrl+Q`: quit the TUI; music keeps playing in the background. `Ctrl+X`: stop playback, shut down the background daemon, and quit.

Playback shortcuts intentionally use `Ctrl`, leaving unmodified letter keys available for list navigation.

### Desktop media keys

On Linux, Sub.NETUI registers an MPRIS player on the D-Bus session bus (`Playback/MprisMediaSession.cs`, using `Tmds.DBus.Protocol`), so desktop shells show the current track and its progress, keyboard media keys (Play/Pause, Next, Previous, Stop) control playback while the terminal is unfocused, and dragging the shell's progress bar seeks within the track.

- The player is published as `org.mpris.MediaPlayer2.subnetui` (inspect it with `busctl --user introspect org.mpris.MediaPlayer2.subnetui /org/mpris/MediaPlayer2`).
- `PlaybackController` publishes track changes, playback state, playback position, and album artwork to the media session, and runs incoming media requests through the same command queue as the TUI.
- If another instance already owns the name, the player is published as `org.mpris.MediaPlayer2.subnetui.instance<pid>`. If no session bus is available, the app falls back to a no-op `NullMediaSession` and keeps working normally.

Not implemented: Windows (SMTC) and macOS (Now Playing). Other operating systems use the no-op media session.

The player displays a useful error if `mpv` is missing or cannot start. The progress bar queries `mpv` once per second and stops updating while playback is paused or stopped.

## Architecture

- `Configuration`: runtime settings, environment-variable precedence, and source-generated JSON serialization.
- `Subsonic`: models, HTTP client, and API errors. Each request creates a random `salt` and the MD5 token required by Subsonic; the same `HttpClient` is reused for the lifetime of the application.
- `Playback`: track queue, `IAudioPlayer`, and `IMediaSession` contracts. `PlaybackController` owns the queue, player, media session, and progress tracking, and serializes every command. `MpvAudioPlayer` manages a headless process and IPC channel for commands and end-of-track events; `MprisMediaSession` exposes playback to Linux desktop controls over D-Bus. `NavidromeSessionSync` reports now playing, scrobbles, and saves and restores the play queue on the server.
- `Daemon`: `subnetui daemon` mode. `PlaybackDaemon` hosts a `PlaybackController` behind a Unix socket; `DaemonProtocol` defines the JSON-lines commands and events.
- `UI`: `MainWindow` coordinates views and selection flow; views do not access HTTP directly. `LibraryListPane<T>` keeps typed models and displays their names in a Terminal.Gui `ListView`.
- `Program`: manual dependency composition (Pure DI), configuration, and Terminal.Gui startup.

### Terminal.Gui v2 concepts in use

- `Application.Create()` creates an application instance. `app.Run(window)` runs the main loop; views and their events must be manipulated on the UI thread.
- Network and player calls are awaited outside the UI thread. Results are applied with `app.Invoke(...)`, which schedules work on the application thread.
- `Pos` describes a position relative to other views and `Dim` describes a size relative to available space. Panes are composed as child views so the layout can change without mixing it with Subsonic logic.
- Navigation is observed through `ListView.ValueChanged`; confirmed actions are handled with `Accepted`. `Accepting` is the cancellable event before acceptance and is best reserved for validating or blocking an action rather than performing side effects.
- Focus belongs to views that receive input; `Tab` cycles through lists and `Enter`/`Esc` move forward or backward. Parent `SuperView`s also need `CanFocus = true` so Terminal.Gui can focus their descendants. Panes are reusable and contain no network logic.
- Selection requests are cancelled when the artist or album changes. Debouncing avoids repeated requests while quickly moving through a list, and stale responses are ignored.

## Extending the application

To add a feature, first add the API type and method in `Subsonic`, register new root types in `SubsonicJsonContext` to preserve AOT compatibility, and expose a view or small service in its layer. Keep HTTP out of views; let `MainWindow` coordinate actions and use `app.Invoke` to apply results. If an operation has side effects, bind it to `Accepted` or a clear shortcut and handle cancellation and API errors.