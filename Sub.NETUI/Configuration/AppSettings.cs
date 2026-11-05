using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sub.NETUI.Configuration;

public sealed record AppSettings(string Url, string Username, string Password);

internal sealed record AppSettingsFile(string? Url, string? Username, string? Password);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettingsFile))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

public static class AppSettingsLoader
{
    public static bool TryLoad(out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        AppSettingsFile? file = null;
        var path = GetConfigPath();

        if (File.Exists(path))
        {
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettingsFile);
            }
            catch (JsonException exception)
            {
                error = $"Could not read the configuration at {path}: {exception.Message}";
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = $"Could not read the configuration at {path}: {exception.Message}";
                return false;
            }
        }

        var (url, username, password) = GetValues(file);
        return TryCreate(url, username, password, out settings, out error);
    }

    public static (string Url, string Username, string Password) ReadDraft()
    {
        AppSettingsFile? file = null;
        var path = GetConfigPath();
        if (File.Exists(path))
        {
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettingsFile);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var values = GetValues(file);
        return (values.Url ?? string.Empty, values.Username ?? string.Empty, values.Password ?? string.Empty);
    }

    public static bool TryCreate(
        string? url,
        string? username,
        string? password,
        out AppSettings? settings,
        out string? error)
    {
        settings = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            error = "Enter the Navidrome URL, username, and password.";
            return false;
        }

        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The URL must be absolute and use the http or https scheme.";
            return false;
        }

        settings = new AppSettings(url.TrimEnd('/'), username, password);
        return true;
    }

    public static void Save(AppSettings settings)
    {
        var path = GetConfigPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var file = new AppSettingsFile(settings.Url, settings.Username, settings.Password);
        var json = JsonSerializer.Serialize(file, AppSettingsJsonContext.Default.AppSettingsFile);
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static (string? Url, string? Username, string? Password) GetValues(AppSettingsFile? file)
    {
        return (
            GetEnvironmentValue("NAVIDROME_URL") ?? file?.Url,
            GetEnvironmentValue("NAVIDROME_USER") ?? file?.Username,
            GetEnvironmentValue("NAVIDROME_PASSWORD") ?? file?.Password);
    }

    private static string GetConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "subnetui",
            "config.json");
    }

    private static string? GetEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}