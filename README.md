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

Native AOT creates a native executable for the runtime specified with `-r`; choose the runtime identifier for your OS and architecture. Configuration and Subsonic responses use `System.Text.Json` source generation to avoid dynamic reflection during publishing. `mpv` is an external process and is not included in the executable.

## Usage

- Arrow keys: navigate artists, albums, and tracks; focus stays in the list at its beginning or end.
- `Tab`: switch between the artists, albums, and tracks panes.
- `Enter` on an artist or album: move to the next pane; `Esc`: return to the previous pane. Arrow keys never switch panes.
- `Enter` on a track: play it and continue with the following tracks on the album.
- Letters: jump to the first artist, album, or track whose name starts with that letter (`ListView` incremental search).
- Digits in the tracks pane: select a track by its track number; type consecutive digits (for example, `1` then `2` for track 12) within one second.
- `Ctrl+Space`: pause or resume; `Ctrl+S`: stop.
- `Ctrl+N` / `Ctrl+P`: next / previous track; `Ctrl+Q`: quit.

Playback shortcuts intentionally use `Ctrl`, leaving unmodified letter keys available for list navigation.

### Desktop media keys (work in progress)

> **Status: not working yet.** The MPRIS integration is implemented but has not been validated end to end. Media keys do not control the application yet.

The goal is to let keyboard media keys (Play/Pause, Next, Previous, Stop) control Sub.NETUI while the terminal is unfocused, by registering an MPRIS player on the Linux D-Bus session bus (`Playback/MprisMediaSession.cs`, using `Tmds.DBus.Protocol`).

What works today:

- The app connects to the session bus at startup and acquires the name `org.mpris.MediaPlayer2.subnetui` (visible with `busctl --user status org.mpris.MediaPlayer2.subnetui`).
- `MainWindow` publishes track changes, playback state, and album artwork to the media session, and routes incoming media requests to the UI thread with `app.Invoke`.
- If no session bus is available, the app falls back to a no-op `NullMediaSession` and keeps working normally.

Known issue:

- Incoming D-Bus calls (for example `gdbus call --session --dest org.mpris.MediaPlayer2.subnetui --object-path /org/mpris/MediaPlayer2 --method org.freedesktop.DBus.Introspectable.Introspect`) time out. A temporary trace showed that `MprisMediaSession.HandleMethodAsync` is never invoked, so the problem is in how the connection dispatches incoming method calls, not in the MPRIS handler logic. Moving the connection setup off the UI thread and using `ConfigureAwait(false)` did not fix it.

Not implemented: Windows (SMTC) and macOS (Now Playing). Other operating systems use the no-op media session.

The player displays a useful error if `mpv` is missing or cannot start. The progress bar queries `mpv` once per second and stops updating while playback is paused or stopped.

## Architecture

- `Configuration`: runtime settings, environment-variable precedence, and source-generated JSON serialization.
- `Subsonic`: models, HTTP client, and API errors. Each request creates a random `salt` and the MD5 token required by Subsonic; the same `HttpClient` is reused for the lifetime of the application.
- `Playback`: track queue, `IAudioPlayer`, and `IMediaSession` contracts. `MpvAudioPlayer` manages a headless process and IPC channel for commands and end-of-track events; `MprisMediaSession` exposes playback to Linux desktop controls over D-Bus.
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