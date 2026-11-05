namespace Sub.NETUI.Subsonic;

public sealed record Song(
    string Id,
    string Title,
    string? Album,
    string? Artist,
    int? Track,
    int? Duration,
    string? Suffix,
    string? ContentType,
    int? DiscNumber,
    string? AlbumId,
    string? ArtistId);