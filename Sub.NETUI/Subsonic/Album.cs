using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

public sealed record Album(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    int SongCount,
    string? CoverArt,
    [property: JsonPropertyName("song")] List<Song>? Songs);