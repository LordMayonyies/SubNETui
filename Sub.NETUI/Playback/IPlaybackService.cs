using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

public interface IPlaybackService
{
    event EventHandler<PlaybackSnapshot>? StateChanged;

    event EventHandler<PlaybackProgress>? ProgressChanged;

    event EventHandler<string>? NoticeRaised;

    event EventHandler? ShutdownRequested;

    PlaybackSnapshot Current { get; }

    PlaybackProgress Progress { get; }

    bool IsActive { get; }

    Task CueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri);

    Task PlayQueueAsync(IReadOnlyList<Song> tracks, int index, Uri? artworkUri);

    Task PlayAsync();

    Task PauseAsync();

    Task TogglePauseAsync();

    Task StopAsync();

    Task NextAsync();

    Task PreviousAsync();

    Task SeekAsync(TimeSpan position);

    Task ShutdownAsync();
}
