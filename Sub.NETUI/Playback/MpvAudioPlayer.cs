using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sub.NETUI.Playback;

public sealed class MpvAudioPlayer : IAudioPlayer
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
    private Process? _process;
    private Stream? _ipcStream;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private string? _ipcAddress;
    private int _requestId;
    private bool _disposed;

    public event EventHandler? TrackEnded;

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public async Task PlayAsync(
        Uri streamUri,
        TimeSpan startPosition = default,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null || _process.HasExited)
            {
                await StartPlayerAsync(cancellationToken);
            }

            // "start" applies to every file loaded afterwards, so it is set on each load.
            string start = Math.Max(0, startPosition.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            await SendCommandAsync(["set_property", "start", start], cancellationToken);
            await SendCommandAsync(["loadfile", streamUri.AbsoluteUri, "replace"], cancellationToken);
            IsPlaying = true;
            IsPaused = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        bool newPauseState = !IsPaused;
        await SendCommandAsync(["set_property", "pause", newPauseState], cancellationToken);
        IsPaused = newPauseState;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        await SendCommandAsync(["stop"], cancellationToken);
        IsPlaying = false;
        IsPaused = false;
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        await SendCommandAsync(
            ["seek", Math.Max(0, position.TotalSeconds), "absolute"],
            cancellationToken);
    }

    public async Task<(TimeSpan? Position, TimeSpan? Duration)> GetProgressAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return (null, null);
        }

        JsonElement position = await SendCommandAsync(["get_property", "time-pos"], cancellationToken);
        JsonElement duration = await SendCommandAsync(["get_property", "duration"], cancellationToken);
        return (ToTimeSpan(position), ToTimeSpan(duration));
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task StartPlayerAsync(CancellationToken cancellationToken)
    {
        string pipeName = $"subnetui-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _ipcAddress = OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{pipeName}"
            : Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");

        var startInfo = new ProcessStartInfo
        {
            FileName = "mpv",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--no-video");
        startInfo.ArgumentList.Add("--idle=yes");
        startInfo.ArgumentList.Add("--force-window=no");
        startInfo.ArgumentList.Add("--terminal=no");
        startInfo.ArgumentList.Add("--demuxer-max-bytes=8MiB");
        startInfo.ArgumentList.Add("--demuxer-max-back-bytes=2MiB");
        startInfo.ArgumentList.Add($"--input-ipc-server={_ipcAddress}");
        if (OperatingSystem.IsMacOS())
        {
            // mpv would otherwise register its own Now Playing entry and take the media keys.
            startInfo.ArgumentList.Add("--input-media-keys=no");
        }

        try
        {
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start mpv.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("mpv was not found. Install it and make sure it is available on PATH.", exception);
        }

        try
        {
            _ipcStream = await ConnectIpcAsync(pipeName, cancellationToken);
            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReadMessagesAsync(_ipcStream, _readerCancellation.Token);
        }
        catch
        {
            await TerminateProcessAsync();
            throw;
        }
    }

    private async Task<Stream> ConnectIpcAsync(string pipeName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (_process is null || _process.HasExited)
                {
                    throw new InvalidOperationException("mpv exited before opening its control channel.");
                }

                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(200, timeout.Token);
                        return pipe;
                    }
                    catch (TimeoutException)
                    {
                        pipe.Dispose();
                    }
                    catch (IOException)
                    {
                        pipe.Dispose();
                    }
                    catch (OperationCanceledException)
                    {
                        pipe.Dispose();
                        throw;
                    }
                }
                else
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_ipcAddress!), timeout.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException)
                    {
                        socket.Dispose();
                    }
                    catch (OperationCanceledException)
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("mpv did not open its IPC channel within five seconds.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("mpv did not open its IPC channel within five seconds.");
    }

    private async Task<JsonElement> SendCommandAsync(object?[] arguments, CancellationToken cancellationToken)
    {
        Stream stream = _ipcStream ?? throw new InvalidOperationException("mpv is not connected.");
        int id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = completion;

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("command");
                writer.WriteStartArray();
                foreach (object? argument in arguments)
                {
                    switch (argument)
                    {
                        case string text:
                            writer.WriteStringValue(text);
                            break;
                        case bool boolean:
                            writer.WriteBooleanValue(boolean);
                            break;
                        case double number:
                            writer.WriteNumberValue(number);
                            break;
                        default:
                            throw new ArgumentException("Unsupported IPC argument type.", nameof(arguments));
                    }
                }

                writer.WriteEndArray();
                writer.WriteNumber("request_id", id);
                writer.WriteEndObject();
            }

            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(buffer.WrittenMemory, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            JsonElement response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (response.TryGetProperty("error", out JsonElement error) && error.GetString() != "success")
            {
                throw new InvalidOperationException($"mpv rejected the command: {error.GetString()}");
            }

            return response;
        }
        finally
        {
            _pendingCommands.TryRemove(id, out _);
        }
    }

    private async Task ReadMessagesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("request_id", out JsonElement requestId) &&
                    requestId.TryGetInt32(out int id) &&
                    _pendingCommands.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion))
                {
                    completion.TrySetResult(root.Clone());
                }
                else if (root.TryGetProperty("event", out JsonElement eventName) &&
                         eventName.GetString() == "end-file" &&
                         root.TryGetProperty("reason", out JsonElement reason) &&
                         reason.GetString() == "eof")
                {
                    IsPlaying = false;
                    IsPaused = false;
                    TrackEnded?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan? ToTimeSpan(JsonElement response)
    {
        if (!response.TryGetProperty("data", out JsonElement data) || !data.TryGetDouble(out double seconds) || seconds < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is { HasExited: false })
        {
            try
            {
                await SendCommandAsync(["quit"], CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
            }

            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        _readerCancellation?.Cancel();
        _ipcStream?.Dispose();
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await TerminateProcessAsync();
        _readerCancellation?.Dispose();
        _lifecycleGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task TerminateProcessAsync()
    {
        _readerCancellation?.Cancel();
        _ipcStream?.Dispose();
        _ipcStream = null;

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        _process = null;

        if (!OperatingSystem.IsWindows() && _ipcAddress is not null)
        {
            try
            {
                File.Delete(_ipcAddress);
            }
            catch (IOException)
            {
            }
        }

        IsPlaying = false;
        IsPaused = false;
    }
}