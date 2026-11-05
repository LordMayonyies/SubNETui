using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

internal sealed record ArtistData(
    string Id,
    string Name,
    [property: JsonPropertyName("album")] List<AlbumSummary>? Albums);