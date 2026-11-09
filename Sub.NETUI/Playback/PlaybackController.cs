using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

public sealed record PlaybackSnapshot(Song? Track, PlaybackState State, string? Message);

public sealed record PlaybackProgress(TimeSpan? Position, TimeSpan? Duration);

public sealed class PlaybackController : IPlaybackService, IDisposable
{
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);
    private readonly IAudioPlayer _audioPlayer;
    private readonly PlaybackQueue _queue;
    private readonly IMediaSession _mediaSession;
    private readonly SubsonicClient _subsonicClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _progressTimer;
    private Uri? _artworkUri;
    private int _cuedIndex = -1;
    private TimeSpan _lastPosition;
    private TimeSpan? _lastDuration;
    private volatile PlaybackSnapshot _current = new(null, PlaybackState.Stopped, null);
    private volatile PlaybackProgress _progress = new(null, null);
    private bool _disposed;

    public PlaybackController(
        IAudioPlayer audioPlayer,
        PlaybackQueue queue,
        IMediaSession mediaSession,
        SubsonicClient subsonicClient)
    {
        _audioPlayer = audioPlayer;
        _queue = queue;
        _mediaSession = mediaSession;
        _subsonicClient = subsonicClient;
        _progressTimer = new Timer(
            _ => _ = UpdateProgressAsync(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _audioPlayer.TrackEnded += OnTrackEnded;
        _mediaSession.PlayPauseRequested += OnMediaPlayPauseRequested;
        _mediaSession.PlayRequested += OnMediaPlayRequested;
        _mediaSession.PauseRequested += OnMediaPauseRequested;
        _mediaSession.NextRequested += OnMediaNextRequested;
        _mediaSession.PreviousRequested += OnMediaPreviousRequested;
        _mediaSession.StopRequested += OnMediaStopRequested;
        _mediaSession.SeekRequested += OnMediaSeekRequested;
        _mediaSession.QuitRequested += OnMediaQuitRequested;
    }

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public event EventHandler<PlaybackProgress>? ProgressChanged;

    public event EventHandler<string>? NoticeRaised;

    public event EventHandler? ShutdownRequested;

    public PlaybackSnapshot Current => _current;

    public PlaybackProgress Progress => _progress;

    public bool IsActive => _current.State != PlaybackState.Stopped;

    public Task CueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri)
    {
        return RunAsync(() =>
        {
            if (!_audioPlayer.IsPlaying)
            {
                _queue.SetTracks(tracks);
                _cuedIndex = index;
                _artworkUri = artworkUri;
            }

            return Task.CompletedTask;
        });
    }

    public Task PlayQueueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri)
    {
        return RunAsync(async () =>
        {
            _queue.SetTracks(tracks);
            _artworkUri = artworkUri;
            await StartAtAsync(index);
        });
    }

    public Task PlayAsync()
    {
        return RunAsync(async () =>
        {
            if (_audioPlayer.IsPaused)
            {
                await SetPausedAsync(false);
            }
            else if (!_audioPlayer.IsPlaying)
            {
                await StartCurrentOrCuedAsync();
            }
        });
    }

    public Task PauseAsync()
    {
        return RunAsync(async () =>
        {
            if (_audioPlayer.IsPlaying && !_audioPlayer.IsPaused)
            {
                await SetPausedAsync(true);
            }
        });
    }

    public Task TogglePauseAsync()
    {
        return RunAsync(async () =>
        {
            if (_audioPlayer.IsPlaying)
            {
                await SetPausedAsync(!_audioPlayer.IsPaused);
            }
            else
            {
                await StartCurrentOrCuedAsync();
            }
        });
    }

    public Task StopAsync()
    {
        return RunAsync(async () =>
        {
            StopProgressTimer();
            await _audioPlayer.StopAsync();
            _lastPosition = TimeSpan.Zero;
            _mediaSession.UpdatePlaybackState(PlaybackState.Stopped);
            Publish(new PlaybackSnapshot(_current.Track, PlaybackState.Stopped, "Playback stopped"));
        });
    }

    public Task NextAsync()
    {
        return RunAsync(() => StartAtAsync(_queue.CurrentIndex + 1));
    }

    public Task PreviousAsync()
    {
        return RunAsync(async () =>
        {
            if (_audioPlayer.IsPlaying && _lastPosition > RestartThreshold)
            {
                await SeekCoreAsync(TimeSpan.Zero);
            }
            else
            {
                await StartAtAsync(_queue.CurrentIndex - 1);
            }
        });
    }

    public Task SeekAsync(TimeSpan position)
    {
        return RunAsync(() => SeekCoreAsync(position));
    }

    public Task ShutdownAsync()
    {
        return StopAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audioPlayer.TrackEnded -= OnTrackEnded;
        _mediaSession.PlayPauseRequested -= OnMediaPlayPauseRequested;
        _mediaSession.PlayRequested -= OnMediaPlayRequested;
        _mediaSession.PauseRequested -= OnMediaPauseRequested;
        _mediaSession.NextRequested -= OnMediaNextRequested;
        _mediaSession.PreviousRequested -= OnMediaPreviousRequested;
        _mediaSession.StopRequested -= OnMediaStopRequested;
        _mediaSession.SeekRequested -= OnMediaSeekRequested;
        _mediaSession.QuitRequested -= OnMediaQuitRequested;
        _progressTimer.Dispose();
    }

    private async Task StartCurrentOrCuedAsync()
    {
        if (_queue.CurrentTrack is not null)
        {
            await StartAtAsync(_queue.CurrentIndex);
        }
        else if (_cuedIndex >= 0)
        {
            await StartAtAsync(_cuedIndex);
        }
    }

    private async Task StartAtAsync(int index)
    {
        Song? song = _queue.StartAt(index);
        if (song is null)
        {
            NoticeRaised?.Invoke(this, "There are no more tracks in that direction.");
            return;
        }

        StopProgressTimer();
        _lastPosition = TimeSpan.Zero;
        _lastDuration = null;
        _progress = new PlaybackProgress(null, null);
        try
        {
            await _audioPlayer.PlayAsync(_subsonicClient.BuildStreamUri(song.Id));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _mediaSession.UpdatePlaybackState(PlaybackState.Stopped);
            Publish(new PlaybackSnapshot(song, PlaybackState.Stopped, $"Could not play track: {exception.Message}"));
            NoticeRaised?.Invoke(this, "Make sure mpv is installed and available on PATH.");
            return;
        }

        _mediaSession.UpdateTrack(song, _artworkUri);
        _mediaSession.UpdatePlaybackState(PlaybackState.Playing);
        Publish(new PlaybackSnapshot(song, PlaybackState.Playing, null));
        StartProgressTimer();
    }

    private async Task SetPausedAsync(bool paused)
    {
        await _audioPlayer.TogglePauseAsync();
        if (_audioPlayer.IsPaused != paused)
        {
            return;
        }

        PlaybackState state = paused ? PlaybackState.Paused : PlaybackState.Playing;
        if (paused)
        {
            StopProgressTimer();
        }
        else
        {
            StartProgressTimer();
        }

        _mediaSession.UpdatePlaybackState(state);
        Publish(_current with { State = state, Message = null });
    }

    private async Task SeekCoreAsync(TimeSpan position)
    {
        if (!_audioPlayer.IsPlaying)
        {
            return;
        }

        await _audioPlayer.SeekAsync(position);
        _lastPosition = position;
        _mediaSession.NotifySeeked(position);
        PublishProgress(new PlaybackProgress(position, _lastDuration));
    }

    private void OnTrackEnded(object? sender, EventArgs eventArgs)
    {
        Dispatch(async () =>
        {
            if (_queue.CurrentIndex + 1 < _queue.Tracks.Count)
            {
                await StartAtAsync(_queue.CurrentIndex + 1);
                return;
            }

            StopProgressTimer();
            _lastPosition = TimeSpan.Zero;
            _mediaSession.UpdatePlaybackState(PlaybackState.Stopped);
            Publish(new PlaybackSnapshot(_current.Track, PlaybackState.Stopped, "End of album"));
        });
    }

    private async Task UpdateProgressAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            (TimeSpan? position, TimeSpan? duration) = await _audioPlayer.GetProgressAsync();
            _lastDuration = duration ?? _lastDuration;
            if (position is { } currentPosition)
            {
                _lastPosition = currentPosition;
                _mediaSession.UpdateProgress(currentPosition, duration);
            }

            PublishProgress(new PlaybackProgress(position, duration));
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(Func<Task> command)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await command();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or TimeoutException or ObjectDisposedException)
        {
            StopProgressTimer();
            NoticeRaised?.Invoke(this, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Dispatch(Func<Task> command)
    {
        _ = Task.Run(() => RunAsync(command));
    }

    private void Publish(PlaybackSnapshot snapshot)
    {
        _current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void PublishProgress(PlaybackProgress progress)
    {
        _progress = progress;
        ProgressChanged?.Invoke(this, progress);
    }

    private void StartProgressTimer()
    {
        if (!_disposed)
        {
            _progressTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    private void StopProgressTimer()
    {
        if (!_disposed)
        {
            _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnMediaPlayPauseRequested(object? sender, EventArgs e) => _ = Task.Run(TogglePauseAsync);

    private void OnMediaPlayRequested(object? sender, EventArgs e) => _ = Task.Run(PlayAsync);

    private void OnMediaPauseRequested(object? sender, EventArgs e) => _ = Task.Run(PauseAsync);

    private void OnMediaNextRequested(object? sender, EventArgs e) => _ = Task.Run(NextAsync);

    private void OnMediaPreviousRequested(object? sender, EventArgs e) => _ = Task.Run(PreviousAsync);

    private void OnMediaStopRequested(object? sender, EventArgs e) => _ = Task.Run(StopAsync);

    private void OnMediaSeekRequested(object? sender, TimeSpan position) => _ = Task.Run(() => SeekAsync(position));

    private void OnMediaQuitRequested(object? sender, EventArgs e) => ShutdownRequested?.Invoke(this, EventArgs.Empty);
}
