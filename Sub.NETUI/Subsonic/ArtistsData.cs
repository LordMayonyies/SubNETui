using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

internal sealed record ArtistsData([property: JsonPropertyName("index")] List<ArtistIndex>? Indices);