using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

internal sealed record ArtistIndex(string Name, [property: JsonPropertyName("artist")] List<Artist> Artists);