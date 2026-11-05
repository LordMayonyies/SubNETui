using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

internal sealed record SubsonicEnvelope(
    [property: JsonPropertyName("subsonic-response")]
    SubsonicResponse? Response);