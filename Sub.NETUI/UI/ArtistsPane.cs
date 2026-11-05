using Sub.NETUI.Subsonic;

namespace Sub.NETUI.UI;

internal sealed class ArtistsPane() : LibraryListPane<Artist>("Artists", artist => artist.Name);