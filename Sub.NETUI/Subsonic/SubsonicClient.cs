using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Sub.NETUI.Configuration;

namespace Sub.NETUI.Subsonic;

public sealed record Artist(string Id, string Name, int AlbumCount);

public sealed record AlbumSummary(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    int SongCount,
    string? CoverArt);

public sealed record Album(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    int SongCount,
    string? CoverArt,
    [property: JsonPropertyName("song")] List<Song>? Songs);

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

public sealed class SubsonicException(string message, int? code = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? Code { get; } = code;
}

internal sealed record SubsonicEnvelope(
    [property: JsonPropertyName("subsonic-response")]
    SubsonicResponse? Response);

internal sealed record SubsonicResponse(
    string? Status,
    SubsonicError? Error,
    ArtistsData? Artists,
    ArtistData? Artist,
    Album? Album);

internal sealed record SubsonicError(int Code, string Message);

internal sealed record ArtistsData([property: JsonPropertyName("index")] List<ArtistIndex>? Indices);

internal sealed record ArtistIndex(string Name, [property: JsonPropertyName("artist")] List<Artist> Artists);

internal sealed record ArtistData(
    string Id,
    string Name,
    [property: JsonPropertyName("album")] List<AlbumSummary>? Albums);

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

public sealed class SubsonicClient(HttpClient httpClient, AppSettings settings)
{
    private const string ApiVersion = "1.16.1";
    private readonly HttpClient _httpClient = httpClient;
    private readonly string _baseUrl = settings.Url.TrimEnd('/');
    private readonly string _username = settings.Username;
    private readonly string _password = settings.Password;

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await GetResponseAsync("ping", null, cancellationToken);
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync("getArtists", null, cancellationToken);
        return response.Response!.Artists?.Indices?
            .SelectMany(index => index.Artists)
            .OrderBy(artist => artist.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];
    }

    public async Task<IReadOnlyList<AlbumSummary>> GetArtistAlbumsAsync(
        string artistId,
        CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync("getArtist", [("id", artistId)], cancellationToken);
        return response.Response!.Artist?.Albums?
            .OrderBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];
    }

    public async Task<Album> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync("getAlbum", [("id", albumId)], cancellationToken);
        var album = response.Response!.Album ??
                      throw new SubsonicException("La respuesta de Navidrome no contiene el álbum solicitado.");
        return album with { Songs = album.Songs ?? [] };
    }

    public Uri BuildStreamUri(string songId)
    {
        return BuildUri("stream", [("id", songId)]);
    }

    private async Task<SubsonicEnvelope> GetResponseAsync(
        string method,
        (string Name, string Value)[]? parameters,
        CancellationToken cancellationToken
    )
    {
        using var httpResponse =
            await _httpClient.GetAsync(
                BuildUri(method, parameters),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        httpResponse.EnsureSuccessStatusCode();
        await using var content = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        var response =
            await System.Text.Json.JsonSerializer.DeserializeAsync(
                content,
                SubsonicJsonContext.Default.SubsonicEnvelope,
                cancellationToken);

        if (response is null)
        {
            throw new SubsonicException("Navidrome devolvió una respuesta vacía o no válida.");
        }

        var apiResponse = response.Response;
        if (apiResponse is null)
        {
            throw new SubsonicException("Navidrome devolvió una respuesta sin el objeto subsonic-response.");
        }

        if (!string.Equals(apiResponse.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var error = apiResponse.Error;
            throw new SubsonicException(error?.Message ?? "Navidrome rechazó la solicitud.", error?.Code);
        }

        return response;
    }

    private Uri BuildUri(string method, (string Name, string Value)[]? parameters)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(12);
        var salt = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_password + salt))).ToLowerInvariant();
        var query = new List<string>
        {
            $"u={Uri.EscapeDataString(_username)}",
            $"t={token}",
            $"s={salt}",
            $"v={ApiVersion}",
            "c=SubNETUI",
            "f=json"
        };

        if (parameters is not null)
        {
            query.AddRange(parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"));
        }

        return new Uri($"{_baseUrl}/rest/{method}?{string.Join('&', query)}", UriKind.Absolute);
    }
}