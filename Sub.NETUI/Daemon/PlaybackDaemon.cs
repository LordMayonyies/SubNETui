using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sub.NETUI.Playback;

namespace Sub.NETUI.Daemon;

public sealed class PlaybackDaemon : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PausedIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly PlaybackController _playback;
    private readonly string _socketPath;
    private readonly string _lockPath;
    private readonly string _pidPath;
    private readonly ConcurrentDictionary<DaemonClient, byte> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _idleTimer;
    private Socket? _listener;

    public PlaybackDaemon(PlaybackController playback, string socketPath, string lockPath, string pidPath)
    {
        _playback = playback;
        _socketPath = socketPath;
        _lockPath = lockPath;
        _pidPath = pidPath;
        _idleTimer = new Timer(_ => OnIdleTimerElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static async Task<bool> IsRunningAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(socketPath))
        {
            return false;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task RunAsync()
    {
        using FileStream? instanceLock = TryAcquireInstanceLock(_lockPath);
        if (instanceLock is null || await IsRunningAsync(_socketPath))
        {
            throw new InvalidOperationException($"Another Sub.NETUI daemon is already running on {_socketPath}.");
        }

        await File.WriteAllTextAsync(_pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Delete(_socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _listener.Listen();
        _playback.StateChanged += OnStateChanged;
        _playback.ProgressChanged += OnProgressChanged;
        _playback.NoticeRaised += OnNoticeRaised;
        _playback.ShutdownRequested += OnShutdownRequested;
        UpdateIdleTimer();

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                Socket socket = await _listener.AcceptAsync(_shutdown.Token);
                var client = new DaemonClient(socket);
                _clients[client] = 0;
                UpdateIdleTimer();
                _ = ServeClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _playback.StateChanged -= OnStateChanged;
            _playback.ProgressChanged -= OnProgressChanged;
            _playback.NoticeRaised -= OnNoticeRaised;
            _playback.ShutdownRequested -= OnShutdownRequested;
            foreach (DaemonClient client in _clients.Keys)
            {
                client.Dispose();
            }

            _listener.Dispose();
            File.Delete(_socketPath);
            File.Delete(_pidPath);
        }
    }

    public void RequestShutdown()
    {
        _shutdown.Cancel();
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _shutdown.Dispose();
    }

    private async Task ServeClientAsync(DaemonClient client)
    {
        try
        {
            // A client may write a command and close right away; a failed greeting must not discard it.
            await SendIgnoringErrorsAsync(client, StateEvent(_playback.Current));
            await SendIgnoringErrorsAsync(client, ProgressEvent(_playback.Progress));
            while (!_shutdown.IsCancellationRequested)
            {
                string? line = await client.Reader.ReadLineAsync(_shutdown.Token);
                if (line is null)
                {
                    break;
                }

                DaemonCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize(line, DaemonJsonContext.Default.DaemonCommand);
                }
                catch (JsonException)
                {
                    await SendIgnoringErrorsAsync(
                        client,
                        new DaemonEvent(DaemonEventType.Notice, Message: "Invalid daemon command."));
                    continue;
                }

                if (command is not null)
                {
                    await ExecuteAsync(command, client);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                              or OperationCanceledException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            UpdateIdleTimer();
        }
    }

    private static async Task SendIgnoringErrorsAsync(DaemonClient client, DaemonEvent daemonEvent)
    {
        try
        {
            await client.SendAsync(daemonEvent);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ExecuteAsync(DaemonCommand command, DaemonClient client)
    {
        switch (command.Type)
        {
            case DaemonCommandType.Cue:
                await _playback.CueAsync(command.Tracks ?? [], command.Index ?? -1, ToUri(command.ArtworkUri));
                break;
            case DaemonCommandType.PlayQueue:
                await _playback.PlayQueueAsync(command.Tracks ?? [], command.Index ?? 0, ToUri(command.ArtworkUri));
                break;
            case DaemonCommandType.Play:
                await _playback.PlayAsync();
                break;
            case DaemonCommandType.Pause:
                await _playback.PauseAsync();
                break;
            case DaemonCommandType.TogglePause:
                await _playback.TogglePauseAsync();
                break;
            case DaemonCommandType.Stop:
                await _playback.StopAsync();
                break;
            case DaemonCommandType.Next:
                await _playback.NextAsync();
                break;
            case DaemonCommandType.Previous:
                await _playback.PreviousAsync();
                break;
            case DaemonCommandType.Seek when command.Position is { } position:
                await _playback.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, position)));
                break;
            case DaemonCommandType.Shutdown:
                await StopAndShutdownAsync();
                break;
            default:
                await SendIgnoringErrorsAsync(
                    client,
                    new DaemonEvent(DaemonEventType.Notice, Message: $"Unknown daemon command '{command.Type}'."));
                break;
        }
    }

    private async Task StopAndShutdownAsync()
    {
        try
        {
            await _playback.StopAsync().WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
        }

        RequestShutdown();
    }

    private void OnShutdownRequested(object? sender, EventArgs e)
    {
        _ = StopAndShutdownAsync();
    }

    private void OnStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        Broadcast(StateEvent(snapshot));
        UpdateIdleTimer();
    }

    private void OnProgressChanged(object? sender, PlaybackProgress progress)
    {
        Broadcast(ProgressEvent(progress));
    }

    private void OnNoticeRaised(object? sender, string message)
    {
        Broadcast(new DaemonEvent(DaemonEventType.Notice, Message: message));
    }

    private void Broadcast(DaemonEvent daemonEvent)
    {
        foreach (DaemonClient client in _clients.Keys)
        {
            _ = SendOrDropAsync(client, daemonEvent);
        }
    }

    private async Task SendOrDropAsync(DaemonClient client, DaemonEvent daemonEvent)
    {
        try
        {
            await client.SendAsync(daemonEvent);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            if (_clients.TryRemove(client, out _))
            {
                client.Dispose();
                UpdateIdleTimer();
            }
        }
    }

    private void UpdateIdleTimer()
    {
        TimeSpan timeout = !_clients.IsEmpty
            ? Timeout.InfiniteTimeSpan
            : _playback.Current.State switch
            {
                PlaybackState.Stopped => IdleTimeout,
                PlaybackState.Paused => PausedIdleTimeout,
                _ => Timeout.InfiniteTimeSpan
            };
        try
        {
            _idleTimer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnIdleTimerElapsed()
    {
        if (_clients.IsEmpty && _playback.Current.State != PlaybackState.Playing)
        {
            RequestShutdown();
        }
    }

    private static DaemonEvent StateEvent(PlaybackSnapshot snapshot)
    {
        return new DaemonEvent(DaemonEventType.State, snapshot.Track, snapshot.State, snapshot.Message);
    }

    private static DaemonEvent ProgressEvent(PlaybackProgress progress)
    {
        return new DaemonEvent(
            DaemonEventType.Progress,
            Position: progress.Position?.TotalSeconds,
            Duration: progress.Duration?.TotalSeconds);
    }

    private static Uri? ToUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private static FileStream? TryAcquireInstanceLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class DaemonClient : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public DaemonClient(Socket socket)
        {
            _stream = new NetworkStream(socket, ownsSocket: true);
            Reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        }

        public StreamReader Reader { get; }

        public async Task SendAsync(DaemonEvent daemonEvent)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(daemonEvent, DaemonJsonContext.Default.DaemonEvent);
            await _writeGate.WaitAsync();
            try
            {
                await _stream.WriteAsync(payload);
                await _stream.WriteAsync("\n"u8.ToArray());
                await _stream.FlushAsync();
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            Reader.Dispose();
            _stream.Dispose();
        }
    }
}
