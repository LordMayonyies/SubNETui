namespace Sub.NETUI.Playback;

public interface IAudioPlayer : IDisposable
{
    event EventHandler? TrackEnded;

    bool IsPlaying { get; }

    bool IsPaused { get; }

    Task PlayAsync(Uri streamUri, CancellationToken cancellationToken = default);

    Task TogglePauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<(TimeSpan? Position, TimeSpan? Duration)> GetProgressAsync(CancellationToken cancellationToken = default);
}