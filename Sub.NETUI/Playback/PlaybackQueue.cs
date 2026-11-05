using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

public sealed class PlaybackQueue
{
    private IReadOnlyList<Song> _tracks = [];

    public IReadOnlyList<Song> Tracks => _tracks;

    public int CurrentIndex { get; private set; } = -1;

    public Song? CurrentTrack => CurrentIndex >= 0 && CurrentIndex < _tracks.Count ? _tracks[CurrentIndex] : null;

    public void SetTracks(IEnumerable<Song> tracks)
    {
        _tracks = tracks.ToArray();
        CurrentIndex = -1;
    }

    public Song? StartAt(int index)
    {
        if (index < 0 || index >= _tracks.Count)
        {
            return null;
        }

        CurrentIndex = index;
        return CurrentTrack;
    }

    public Song? Next()
    {
        return StartAt(CurrentIndex + 1);
    }

    public Song? Previous()
    {
        return StartAt(CurrentIndex - 1);
    }
}