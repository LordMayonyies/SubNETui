using Sub.NETUI.Subsonic;

namespace Sub.NETUI.UI;

internal sealed class AlbumsPane() : LibraryListPane<AlbumSummary>("Albums", album => album.Name);