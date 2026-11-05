namespace Sub.NETUI.Subsonic;

public sealed record AlbumSummary(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    int SongCount,
    string? CoverArt);