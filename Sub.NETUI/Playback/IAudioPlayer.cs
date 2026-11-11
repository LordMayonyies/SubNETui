using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

public interface IAudioPlayer : IDisposable
{
    event EventHandler? TrackEnded;

    bool IsPlaying { get; }

    bool IsPaused { get; }

    /// <summary>The format Navidrome should transcode the song to, or <c>null</c> to stream the original file.</summary>
    string? GetStreamFormat(Song song) => null;

    Task PlayAsync(Uri streamUri, TimeSpan startPosition = default, CancellationToken cancellationToken = default);

    Task TogglePauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task<(TimeSpan? Position, TimeSpan? Duration)> GetProgressAsync(CancellationToken cancellationToken = default);
}