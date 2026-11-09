using System.Diagnostics;
using System.Text.Json;
using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

// Reports "now playing", scrobbles, and saves the play queue on Navidrome. Server errors are ignored on purpose:
// they must never interrupt playback.
public sealed class NavidromeSessionSync : IDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxScrobbleDelay = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan MaxTickAdvance = TimeSpan.FromSeconds(3);
    private readonly PlaybackController _playback;
    private readonly SubsonicClient _subsonicClient;
    private readonly CancellationTokenSource _disposal = new();
    private readonly Lock _saveLock = new();
    private readonly Stopwatch _sinceLastSave = Stopwatch.StartNew();
    private SavedQueue? _pendingSave;
    private bool _saving;
    private Task _saveWorker = Task.CompletedTask;
    private string? _scrobbleTrackId;
    private DateTimeOffset _scrobbleStartedAt;
    private bool _scrobbled;
    private TimeSpan _listened;
    private TimeSpan? _lastTickPosition;

    public NavidromeSessionSync(PlaybackController playback, SubsonicClient subsonicClient)
    {
        _playback = playback;
        _subsonicClient = subsonicClient;
        _playback.StateChanged += OnStateChanged;
        _playback.ProgressChanged += OnProgressChanged;
    }

    public async Task RestoreAsync()
    {
        try
        {
            PlayQueue? queue = await _subsonicClient.GetPlayQueueAsync(_disposal.Token);
            if (queue?.Entries is not { Count: > 0 } entries)
            {
                return;
            }

            int index = Math.Max(0, entries.FindIndex(song => song.Id == queue.Current));
            Song song = entries[index];
            TimeSpan position = TimeSpan.FromMilliseconds(Math.Max(0, queue.Position ?? 0));
            await _playback.RestoreAsync(
                entries,
                index,
                position,
                _subsonicClient.BuildCoverArtUri(song.CoverArt ?? song.AlbumId));
        }
        catch (Exception exception) when (IsServerError(exception))
        {
        }
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        RequestSave(Capture(_playback.Current));
        Task worker;
        lock (_saveLock)
        {
            worker = _saveWorker;
        }

        try
        {
            await worker.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
        }
    }

    public void Dispose()
    {
        _playback.StateChanged -= OnStateChanged;
        _playback.ProgressChanged -= OnProgressChanged;
        _disposal.Cancel();
    }

    // Playback events are raised inside the controller's command gate, so the queue read here is consistent.
    private void OnStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        if (snapshot is { State: PlaybackState.Playing, Track: { } track })
        {
            if (track.Id != _scrobbleTrackId)
            {
                _scrobbleTrackId = track.Id;
                _scrobbleStartedAt = DateTimeOffset.UtcNow;
                _scrobbled = false;
                _listened = TimeSpan.Zero;
            }

            _lastTickPosition = null;

            Send(token => _subsonicClient.ScrobbleAsync(track.Id, false, _scrobbleStartedAt, token));
        }
        else if (snapshot.State == PlaybackState.Stopped)
        {
            _scrobbleTrackId = null;
        }

        RequestSave(Capture(snapshot));
    }

    private void OnProgressChanged(object? sender, PlaybackProgress progress)
    {
        PlaybackSnapshot snapshot = _playback.Current;
        if (snapshot.State != PlaybackState.Playing || progress.Position is not { } position)
        {
            return;
        }

        // Only time actually listened counts towards a scrobble; seeks are larger jumps and are skipped.
        TimeSpan advance = position - (_lastTickPosition ?? position);
        if (advance > TimeSpan.Zero && advance <= MaxTickAdvance)
        {
            _listened += advance;
        }

        _lastTickPosition = position;
        if (!_scrobbled && _scrobbleTrackId is { } trackId && _listened >= ScrobbleThreshold(progress.Duration))
        {
            _scrobbled = true;
            DateTimeOffset startedAt = _scrobbleStartedAt;
            Send(token => _subsonicClient.ScrobbleAsync(trackId, true, startedAt, token));
        }

        if (_sinceLastSave.Elapsed >= SaveInterval)
        {
            RequestSave(Capture(snapshot));
        }
    }

    private static TimeSpan ScrobbleThreshold(TimeSpan? duration)
    {
        return duration is { } length && length / 2 < MaxScrobbleDelay ? length / 2 : MaxScrobbleDelay;
    }

    private SavedQueue? Capture(PlaybackSnapshot snapshot)
    {
        if (snapshot is { State: PlaybackState.Stopped, Message: PlaybackController.EndOfQueueMessage })
        {
            return SavedQueue.Empty;
        }

        IReadOnlyList<Song> tracks = _playback.QueueTracks;
        int index = _playback.QueueIndex;
        if (snapshot.Track is null || index < 0 || index >= tracks.Count)
        {
            return null;
        }

        TimeSpan position = snapshot.State == PlaybackState.Stopped
            ? TimeSpan.Zero
            : _playback.Progress.Position ?? TimeSpan.Zero;
        return new SavedQueue(tracks.Select(song => song.Id).ToArray(), tracks[index].Id, position);
    }

    private void RequestSave(SavedQueue? queue)
    {
        if (queue is null)
        {
            return;
        }

        _sinceLastSave.Restart();
        lock (_saveLock)
        {
            _pendingSave = queue;
            if (!_saving)
            {
                _saving = true;
                _saveWorker = Task.Run(SaveLoopAsync);
            }
        }
    }

    private async Task SaveLoopAsync()
    {
        while (true)
        {
            SavedQueue queue;
            lock (_saveLock)
            {
                if (_pendingSave is null)
                {
                    _saving = false;
                    return;
                }

                queue = _pendingSave;
                _pendingSave = null;
            }

            try
            {
                await _subsonicClient.SavePlayQueueAsync(queue.SongIds, queue.CurrentId, queue.Position, _disposal.Token);
            }
            catch (Exception exception) when (IsServerError(exception))
            {
            }
        }
    }

    private void Send(Func<CancellationToken, Task> request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await request(_disposal.Token);
            }
            catch (Exception exception) when (IsServerError(exception))
            {
            }
        });
    }

    private static bool IsServerError(Exception exception)
    {
        return exception is HttpRequestException or SubsonicException or JsonException
            or OperationCanceledException or ObjectDisposedException;
    }

    private sealed record SavedQueue(IReadOnlyList<string> SongIds, string? CurrentId, TimeSpan Position)
    {
        public static SavedQueue Empty { get; } = new([], null, TimeSpan.Zero);
    }
}
