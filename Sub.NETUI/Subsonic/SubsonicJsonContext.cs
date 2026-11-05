using System.Text.Json.Serialization;

namespace Sub.NETUI.Subsonic;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SubsonicEnvelope))]
[JsonSerializable(typeof(Artist))]
[JsonSerializable(typeof(AlbumSummary))]
[JsonSerializable(typeof(Album))]
[JsonSerializable(typeof(Song))]
[JsonSerializable(typeof(SubsonicError))]
[JsonSerializable(typeof(ArtistsData))]
[JsonSerializable(typeof(ArtistData))]
internal partial class SubsonicJsonContext : JsonSerializerContext;