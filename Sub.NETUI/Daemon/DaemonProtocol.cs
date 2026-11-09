using System.Text.Json.Serialization;
using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Daemon;

public static class DaemonCommandType
{
    public const string Cue = "cue";
    public const string PlayQueue = "play-queue";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string TogglePause = "toggle-pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Seek = "seek";
    public const string Shutdown = "shutdown";
}

public static class DaemonEventType
{
    public const string State = "state";
    public const string Progress = "progress";
    public const string Notice = "notice";
}

public sealed record DaemonCommand(
    string Type,
    IReadOnlyList<Song>? Tracks = null,
    int? Index = null,
    string? ArtworkUri = null,
    double? Position = null);

public sealed record DaemonEvent(
    string Type,
    Song? Track = null,
    PlaybackState? State = null,
    string? Message = null,
    double? Position = null,
    double? Duration = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DaemonCommand))]
[JsonSerializable(typeof(DaemonEvent))]
internal partial class DaemonJsonContext : JsonSerializerContext;

public static class DaemonEndpoint
{
    public static string SocketPath => GetPath(".sock");

    public static string LockPath => GetPath(".lock");

    public static string PidPath => GetPath(".pid");

    private static string GetPath(string extension)
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtimeDirectory)
            ? Path.Combine(Path.GetTempPath(), $"subnetui-{Environment.UserName}{extension}")
            : Path.Combine(runtimeDirectory, $"subnetui{extension}");
    }
}
