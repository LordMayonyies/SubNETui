using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Daemon;

public sealed class DaemonPlaybackClient : IPlaybackService, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private readonly string _socketPath;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _disposal = new();
    private NetworkStream? _stream;
    private volatile PlaybackSnapshot _current = new(null, PlaybackState.Stopped, null);
    private volatile PlaybackProgress _progress = new(null, null);

    private DaemonPlaybackClient(string socketPath)
    {
        _socketPath = socketPath;
    }

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public event EventHandler<PlaybackProgress>? ProgressChanged;

    public event EventHandler<string>? NoticeRaised;

    public event EventHandler? ShutdownRequested
    {
        add { }
        remove { }
    }

    public PlaybackSnapshot Current => _current;

    public PlaybackProgress Progress => _progress;

    public bool IsActive => _current.State != PlaybackState.Stopped;

    public static async Task<DaemonPlaybackClient> ConnectAsync(string socketPath)
    {
        var client = new DaemonPlaybackClient(socketPath);
        try
        {
            await client.EnsureConnectedAsync();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task CueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri)
    {
        return SendAsync(new DaemonCommand(DaemonCommandType.Cue, tracks, index, artworkUri?.AbsoluteUri));
    }

    public Task PlayQueueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri)
    {
        return SendAsync(new DaemonCommand(DaemonCommandType.PlayQueue, tracks, index, artworkUri?.AbsoluteUri));
    }

    public Task PlayAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Play));

    public Task PauseAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Pause));

    public Task TogglePauseAsync() => SendAsync(new DaemonCommand(DaemonCommandType.TogglePause));

    public Task StopAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Stop));

    public Task NextAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Next));

    public Task PreviousAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Previous));

    public Task SeekAsync(TimeSpan position)
    {
        return SendAsync(new DaemonCommand(DaemonCommandType.Seek, Position: position.TotalSeconds));
    }

    public Task ShutdownAsync() => SendAsync(new DaemonCommand(DaemonCommandType.Shutdown));

    public void Dispose()
    {
        if (_disposal.IsCancellationRequested)
        {
            return;
        }

        _disposal.Cancel();
        _stream?.Dispose();
    }

    private async Task SendAsync(DaemonCommand command)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command, DaemonJsonContext.Default.DaemonCommand);
        try
        {
            NetworkStream stream = await EnsureConnectedAsync();
            await _connectionGate.WaitAsync(_disposal.Token);
            try
            {
                await stream.WriteAsync(payload, _disposal.Token);
                await stream.WriteAsync("\n"u8.ToArray(), _disposal.Token);
                await stream.FlushAsync(_disposal.Token);
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                              or TimeoutException or InvalidOperationException)
        {
            NoticeRaised?.Invoke(this, $"Playback daemon unavailable: {exception.Message}");
        }
    }

    private async Task<NetworkStream> EnsureConnectedAsync()
    {
        await _connectionGate.WaitAsync(_disposal.Token);
        try
        {
            if (_stream is { } existing)
            {
                return existing;
            }

            Socket socket = await ConnectOrStartDaemonAsync();
            var stream = new NetworkStream(socket, ownsSocket: true);
            _stream = stream;
            _ = ReadEventsAsync(stream);
            return stream;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<Socket> ConnectOrStartDaemonAsync()
    {
        if (await TryConnectAsync() is { } socket)
        {
            return socket;
        }

        StartDaemon();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartTimeout)
        {
            await Task.Delay(100, _disposal.Token);
            if (await TryConnectAsync() is { } started)
            {
                return started;
            }
        }

        throw new TimeoutException("The playback daemon did not start within five seconds.");
    }

    private async Task<Socket?> TryConnectAsync()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), _disposal.Token);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static void StartDaemon()
    {
        string executable = Environment.ProcessPath
                            ?? throw new InvalidOperationException("Cannot locate the Sub.NETUI executable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("daemon");
        startInfo.ArgumentList.Add("--detach");
        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start the playback daemon.");
    }

    private async Task ReadEventsAsync(NetworkStream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(_disposal.Token) is { } line)
            {
                DaemonEvent? daemonEvent;
                try
                {
                    daemonEvent = JsonSerializer.Deserialize(line, DaemonJsonContext.Default.DaemonEvent);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (daemonEvent is not null)
                {
                    Apply(daemonEvent);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                              or OperationCanceledException)
        {
        }

        if (_disposal.IsCancellationRequested)
        {
            return;
        }

        await _connectionGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_stream, stream))
            {
                _stream = null;
            }
        }
        finally
        {
            _connectionGate.Release();
        }

        stream.Dispose();
        Apply(new DaemonEvent(DaemonEventType.State, State: PlaybackState.Stopped,
            Message: "Playback daemon disconnected"));
    }

    private void Apply(DaemonEvent daemonEvent)
    {
        switch (daemonEvent.Type)
        {
            case DaemonEventType.State:
                var snapshot = new PlaybackSnapshot(
                    daemonEvent.Track,
                    daemonEvent.State ?? PlaybackState.Stopped,
                    daemonEvent.Message);
                _current = snapshot;
                StateChanged?.Invoke(this, snapshot);
                break;
            case DaemonEventType.Progress:
                var progress = new PlaybackProgress(ToTimeSpan(daemonEvent.Position), ToTimeSpan(daemonEvent.Duration));
                _progress = progress;
                ProgressChanged?.Invoke(this, progress);
                break;
            case DaemonEventType.Notice when daemonEvent.Message is { } message:
                NoticeRaised?.Invoke(this, message);
                break;
        }
    }

    private static TimeSpan? ToTimeSpan(double? seconds)
    {
        return seconds is { } value ? TimeSpan.FromSeconds(value) : null;
    }
}
