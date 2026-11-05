using Sub.NETUI.Subsonic;

namespace Sub.NETUI.UI;

internal sealed class ArtistsPane() : LibraryListPane<Artist>("Artistas", artist => artist.Name);