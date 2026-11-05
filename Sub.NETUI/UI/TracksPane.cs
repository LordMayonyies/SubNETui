using Sub.NETUI.Subsonic;

namespace Sub.NETUI.UI;

internal sealed class TracksPane() : LibraryListPane<Song>(
    "Canciones",
    song => song.Track is { } track ? $"{track,2}. {song.Title}" : song.Title);