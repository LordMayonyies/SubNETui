using System.Diagnostics;
using Sub.NETUI.Subsonic;
using Tmds.DBus.Protocol;

namespace Sub.NETUI.Playback;

internal sealed class MprisMediaSession : IMediaSession, IPathMethodHandler
{
    private const string BusName = "org.mpris.MediaPlayer2.subnetui";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string MediaPlayerInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private static readonly ReadOnlyMemory<byte> MediaPlayerInterfaceXml =
        """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        """u8.ToArray();

    private static readonly ReadOnlyMemory<byte> PlayerInterfaceXml =
        """
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Shuffle" type="b" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="read"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
          <signal name="Seeked">
            <arg type="x"/>
          </signal>
        </interface>
        """u8.ToArray();

    private static readonly ReadOnlyMemory<byte> PropertiesInterfaceXml =
        """
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get">
            <arg direction="in" type="s"/>
            <arg direction="in" type="s"/>
            <arg direction="out" type="v"/>
          </method>
          <method name="GetAll">
            <arg direction="in" type="s"/>
            <arg direction="out" type="a{sv}"/>
          </method>
          <method name="Set">
            <arg direction="in" type="s"/>
            <arg direction="in" type="s"/>
            <arg direction="in" type="v"/>
          </method>
          <signal name="PropertiesChanged">
            <arg type="s"/>
            <arg type="a{sv}"/>
            <arg type="as"/>
          </signal>
        </interface>
        """u8.ToArray();

    private readonly DBusConnection _connection;
    private readonly object _stateLock = new();
    private Song? _song;
    private Uri? _artworkUri;
    private PlaybackState _playbackState = PlaybackState.Stopped;
    private TimeSpan _position;
    private long _positionTimestamp;
    private string _trackObjectPath = "/org/mpris/MediaPlayer2/TrackList/NoTrack";
    private bool _disposed;

    private MprisMediaSession(DBusConnection connection)
    {
        _connection = connection;
    }

    public string Path => ObjectPath;

    public bool HandlesChildPaths => false;

    public event EventHandler? PlayPauseRequested;

    public event EventHandler? PlayRequested;

    public event EventHandler? PauseRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PreviousRequested;

    public event EventHandler? StopRequested;

    public static Task<IMediaSession> CreateAsync()
    {
        return Task.Run(CreateCoreAsync);
    }

    private static async Task<IMediaSession> CreateCoreAsync()
    {
        string? address = DBusAddress.Session;
        if (!OperatingSystem.IsLinux() || address is null)
        {
            return new NullMediaSession();
        }

        DBusConnection? connection = null;
        try
        {
            connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            uint result = await RequestBusNameAsync(connection).ConfigureAwait(false);
            if (result != 1)
            {
                connection.Dispose();
                return new NullMediaSession();
            }

            var session = new MprisMediaSession(connection);
            connection.AddMethodHandler(session);
            return session;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            connection?.Dispose();
            return new NullMediaSession();
        }
    }

    public void UpdateTrack(Song? song, Uri? artworkUri)
    {
        Dictionary<string, VariantValue> changed;
        lock (_stateLock)
        {
            _song = song;
            _artworkUri = artworkUri;
            _position = TimeSpan.Zero;
            _positionTimestamp = Stopwatch.GetTimestamp();
            _trackObjectPath = song is null
                ? "/org/mpris/MediaPlayer2/TrackList/NoTrack"
                : $"/org/mpris/MediaPlayer2/track/{Guid.NewGuid():N}";
            changed = new Dictionary<string, VariantValue>
            {
                ["Metadata"] = CreateMetadata()
            };
        }

        EmitPropertiesChanged(PlayerInterface, changed);
    }

    public void UpdatePosition(TimeSpan position)
    {
        long positionMicroseconds = Math.Max(0, position.Ticks / 10);
        bool seeked;
        lock (_stateLock)
        {
            long currentPositionMicroseconds = GetPositionMicrosecondsLocked();
            seeked = Math.Abs(positionMicroseconds - currentPositionMicroseconds) > 2_000_000;
            _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            _positionTimestamp = Stopwatch.GetTimestamp();
        }

        if (seeked)
        {
            EmitSeeked(positionMicroseconds);
        }
    }

    public void UpdatePlaybackState(PlaybackState state)
    {
        lock (_stateLock)
        {
            if (state == PlaybackState.Paused && _playbackState == PlaybackState.Playing)
            {
                _position = GetCurrentPositionLocked();
                _positionTimestamp = Stopwatch.GetTimestamp();
            }
            else if (state == PlaybackState.Stopped)
            {
                _position = TimeSpan.Zero;
                _positionTimestamp = Stopwatch.GetTimestamp();
            }
            else if (state == PlaybackState.Playing && _playbackState != PlaybackState.Playing)
            {
                _positionTimestamp = Stopwatch.GetTimestamp();
            }

            _playbackState = state;
        }

        EmitPropertiesChanged(
            PlayerInterface,
            new Dictionary<string, VariantValue> { ["PlaybackStatus"] = PlaybackStatusValue(state) });
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MediaPlayerInterfaceXml, PlayerInterfaceXml, PropertiesInterfaceXml]);
            return default;
        }

        if (context.IsPropertiesInterfaceRequest)
        {
            HandlePropertiesRequest(context);
            return default;
        }

        var request = context.Request;
        if (request.InterfaceAsString == MediaPlayerInterface && request.SignatureAsString == "")
        {
            switch (request.MemberAsString)
            {
                case "Raise":
                case "Quit":
                    ReplyEmpty(context);
                    return default;
            }
        }

        if (request.InterfaceAsString == PlayerInterface)
        {
            HandlePlayerRequest(context, request.MemberAsString, request.SignatureAsString);
            return default;
        }

        context.ReplyUnknownMethodError();
        return default;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _connection.Dispose();
    }

    private static async Task<uint> RequestBusNameAsync(DBusConnection connection)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "RequestName",
                signature: "su");
            writer.WriteString(BusName);
            writer.WriteUInt32(4);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(
            message,
            static (reply, _) => reply.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
    }

    private void HandlePropertiesRequest(MethodContext context)
    {
        var request = context.Request;
        var reader = request.GetBodyReader();
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case ("Get", "ss"):
            {
                string interfaceName = reader.ReadString();
                string propertyName = reader.ReadString();
                if (!TryGetProperties(interfaceName, out Dictionary<string, VariantValue> properties) ||
                    !properties.TryGetValue(propertyName, out VariantValue propertyValue))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"Unknown property '{propertyName}'.");
                    return;
                }

                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariant(propertyValue);
                context.Reply(writer.CreateMessage());
                return;
            }
            case ("GetAll", "s"):
            {
                string interfaceName = reader.ReadString();
                if (!TryGetProperties(interfaceName, out Dictionary<string, VariantValue> properties))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", $"Unknown interface '{interfaceName}'.");
                    return;
                }

                using var writer = context.CreateReplyWriter("a{sv}");
                writer.WriteDictionary(properties);
                context.Reply(writer.CreateMessage());
                return;
            }
            case ("Set", "ssv"):
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "MPRIS properties are read-only.");
                return;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
    }

    private void HandlePlayerRequest(MethodContext context, string? methodName, string? signature)
    {
        if (signature == "")
        {
            EventHandler? requested = methodName switch
            {
                "PlayPause" => PlayPauseRequested,
                "Play" => PlayRequested,
                "Pause" => PauseRequested,
                "Next" => NextRequested,
                "Previous" => PreviousRequested,
                "Stop" => StopRequested,
                _ => null
            };

            if (requested is null)
            {
                context.ReplyUnknownMethodError();
                return;
            }

            InvokeSafely(requested);
            ReplyEmpty(context);
            return;
        }

        context.ReplyUnknownMethodError();
    }

    private bool TryGetProperties(string interfaceName, out Dictionary<string, VariantValue> properties)
    {
        lock (_stateLock)
        {
            if (interfaceName == MediaPlayerInterface)
            {
                properties = new Dictionary<string, VariantValue>
                {
                    ["CanQuit"] = false,
                    ["CanRaise"] = false,
                    ["HasTrackList"] = false,
                    ["Identity"] = "Sub.NETUI",
                    ["DesktopEntry"] = "subnetui",
                    ["SupportedUriSchemes"] = new Tmds.DBus.Protocol.Array<string>(["http", "https"]),
                    ["SupportedMimeTypes"] = new Tmds.DBus.Protocol.Array<string>([])
                };
                return true;
            }

            if (interfaceName == PlayerInterface)
            {
                properties = new Dictionary<string, VariantValue>
                {
                    ["PlaybackStatus"] = PlaybackStatusValue(_playbackState),
                    ["LoopStatus"] = "None",
                    ["Rate"] = 1.0,
                    ["Shuffle"] = false,
                    ["Metadata"] = CreateMetadata(),
                    ["Volume"] = 1.0,
                    ["Position"] = GetPositionMicrosecondsLocked(),
                    ["MinimumRate"] = 1.0,
                    ["MaximumRate"] = 1.0,
                    ["CanGoNext"] = true,
                    ["CanGoPrevious"] = true,
                    ["CanPlay"] = _song is not null,
                    ["CanPause"] = true,
                    ["CanSeek"] = false,
                    ["CanControl"] = true
                };
                return true;
            }

            properties = [];
            return false;
        }
    }

    private VariantValue CreateMetadata()
    {
        if (_song is null)
        {
            return new Dict<string, VariantValue>
            {
                ["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath(_trackObjectPath))
            };
        }

        var metadata = new Dict<string, VariantValue>
        {
            ["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath(_trackObjectPath)),
            ["xesam:title"] = _song.Title,
            ["xesam:artist"] = new Tmds.DBus.Protocol.Array<string>([_song.Artist ?? string.Empty]),
            ["mpris:length"] = (long)(_song.Duration ?? 0) * 1_000_000
        };
        if (!string.IsNullOrWhiteSpace(_song.Album))
        {
            metadata["xesam:album"] = _song.Album;
        }

        if (_artworkUri is not null)
        {
            metadata["mpris:artUrl"] = _artworkUri.AbsoluteUri;
        }

        return metadata;
    }

    private void EmitPropertiesChanged(string interfaceName, Dictionary<string, VariantValue> changed)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(
                destination: null,
                path: ObjectPath,
                @interface: PropertiesInterface,
                member: "PropertiesChanged",
                signature: "sa{sv}as");
            writer.WriteString(interfaceName);
            writer.WriteDictionary(changed);
            writer.WriteArray(Array.Empty<string>());
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private void EmitSeeked(long positionMicroseconds)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(
                destination: null,
                path: ObjectPath,
                @interface: PlayerInterface,
                member: "Seeked",
                signature: "x");
            writer.WriteInt64(positionMicroseconds);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private long GetPositionMicrosecondsLocked()
    {
        return Math.Max(0, GetCurrentPositionLocked().Ticks / 10);
    }

    private TimeSpan GetCurrentPositionLocked()
    {
        return _playbackState == PlaybackState.Playing
            ? _position + Stopwatch.GetElapsedTime(_positionTimestamp)
            : _position;
    }

    private static VariantValue PlaybackStatusValue(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            _ => "Stopped"
        };
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(string.Empty);
        context.Reply(writer.CreateMessage());
    }

    private static void InvokeSafely(EventHandler handler)
    {
        try
        {
            handler.Invoke(null, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }
}