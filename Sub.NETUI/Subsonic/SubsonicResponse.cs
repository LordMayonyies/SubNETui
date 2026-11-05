namespace Sub.NETUI.Subsonic;

internal sealed record SubsonicResponse(
    string? Status,
    SubsonicError? Error,
    ArtistsData? Artists,
    ArtistData? Artist,
    Album? Album);