using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

public enum PlaybackState
{
    Playing,
    Paused,
    Stopped
}

public interface IMediaSession : IDisposable
{
    event EventHandler? PlayPauseRequested;

    event EventHandler? PlayRequested;

    event EventHandler? PauseRequested;

    event EventHandler? NextRequested;

    event EventHandler? PreviousRequested;

    event EventHandler? StopRequested;

    event EventHandler<TimeSpan>? SeekRequested;

    event EventHandler? QuitRequested;

    void UpdateTrack(Song? song, Uri? artworkUri);

    void UpdatePlaybackState(PlaybackState state);

    void UpdateProgress(TimeSpan position, TimeSpan? duration);

    void NotifySeeked(TimeSpan position);
}

internal sealed class NullMediaSession : IMediaSession
{
    public event EventHandler? PlayPauseRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PlayRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PauseRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? NextRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PreviousRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? StopRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<TimeSpan>? SeekRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? QuitRequested
    {
        add { }
        remove { }
    }

    public void UpdateTrack(Song? song, Uri? artworkUri)
    {
    }

    public void UpdatePlaybackState(PlaybackState state)
    {
    }

    public void UpdateProgress(TimeSpan position, TimeSpan? duration)
    {
    }

    public void NotifySeeked(TimeSpan position)
    {
    }

    public void Dispose()
    {
    }
}