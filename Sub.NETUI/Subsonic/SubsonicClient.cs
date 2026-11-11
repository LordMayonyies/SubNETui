using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sub.NETUI.Configuration;

namespace Sub.NETUI.Subsonic;

public sealed class SubsonicClient(HttpClient httpClient, AppSettings settings)
{
    private const string ApiVersion = "1.16.1";
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
        return response.Response!.Album ??
               throw new SubsonicException("The Navidrome response does not contain the requested album.");
    }

    public async Task ScrobbleAsync(
        string songId,
        bool submission,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await GetResponseAsync(
            "scrobble",
            [
                ("id", songId),
                ("submission", submission ? "true" : "false"),
                ("time", startedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            ],
            cancellationToken);
    }

    public async Task<PlayQueue?> GetPlayQueueAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync("getPlayQueue", null, cancellationToken);
        return response.Response!.PlayQueue;
    }

    public async Task SavePlayQueueAsync(
        IReadOnlyList<string> songIds,
        string? currentId,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        var parameters = songIds.Select(id => ("id", id)).ToList();
        if (currentId is not null)
        {
            parameters.Add(("current", currentId));
            parameters.Add(("position",
                ((long)position.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
        }

        await GetResponseAsync("savePlayQueue", parameters.ToArray(), cancellationToken);
    }

    public Uri BuildStreamUri(string songId, string? format = null)
    {
        return string.IsNullOrEmpty(format)
            ? BuildUri("stream", [("id", songId)])
            : BuildUri("stream", [("id", songId), ("format", format)]);
    }

    public Uri? BuildCoverArtUri(string? coverArtId)
    {
        return string.IsNullOrWhiteSpace(coverArtId)
            ? null
            : BuildUri("getCoverArt", [("id", coverArtId)]);
    }

    private async Task<SubsonicEnvelope> GetResponseAsync(
        string method,
        (string Name, string Value)[]? parameters,
        CancellationToken cancellationToken
    )
    {
        using var httpResponse =
            await httpClient.GetAsync(
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
            throw new SubsonicException("Navidrome returned an empty or invalid response.");
        }

        var apiResponse = response.Response;
        if (apiResponse is null)
        {
            throw new SubsonicException("Navidrome returned a response without a subsonic-response object.");
        }

        if (!string.Equals(apiResponse.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var error = apiResponse.Error;
            throw new SubsonicException(error?.Message ?? "Navidrome rejected the request.", error?.Code);
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