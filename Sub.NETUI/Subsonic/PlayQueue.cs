using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

public sealed record PlayQueue(
    [property: JsonPropertyName("entry")] List<Song>? Entries,
    string? Current,
    long? Position);
