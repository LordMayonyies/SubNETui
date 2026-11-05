using Sub.NETUI.Subsonic;

namespace Sub.NETUI.UI;

internal sealed class AlbumsPane() : LibraryListPane<AlbumSummary>("Álbumes", album => album.Name);