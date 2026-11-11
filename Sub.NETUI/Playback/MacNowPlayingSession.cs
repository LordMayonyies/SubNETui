using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sub.NETUI.Platform.MacOS;
using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

/// <summary>
/// Publishes playback to the macOS Now Playing widget and receives media keys through
/// MPNowPlayingInfoCenter and MPRemoteCommandCenter. All Objective-C calls run on the main dispatch queue.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacNowPlayingSession : IMediaSession
{
    private const string MediaPlayerPath = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";
    private const string TargetClassName = "SubNETUIRemoteCommandTarget";
    private const nint HandlerSuccess = 0;
    private const nint HandlerFailed = 200;
    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    private static readonly TimeSpan ProgressTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(1);
    private static readonly HttpClient ArtworkClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Lock ArtworkImageLock = new();

    private static readonly (string Command, string Selector)[] Commands =
    [
        ("playCommand", "handlePlay:"),
        ("pauseCommand", "handlePause:"),
        ("togglePlayPauseCommand", "handleTogglePlayPause:"),
        ("nextTrackCommand", "handleNextTrack:"),
        ("previousTrackCommand", "handlePreviousTrack:"),
        ("stopCommand", "handleStop:"),
        ("changePlaybackPositionCommand", "handleChangePlaybackPosition:")
    ];

    private static MacNowPlayingSession? _current;
    private static IntPtr _artworkImage;
    private static IntPtr _artworkBlock;

    private readonly Lock _stateLock = new();
    private readonly NowPlayingKeys _keys;
    private IntPtr _target;
    private IntPtr _artwork;
    private Song? _song;
    private TimeSpan? _length;
    private TimeSpan _position;
    private long _positionTimestamp = Stopwatch.GetTimestamp();
    private PlaybackState _playbackState = PlaybackState.Stopped;
    private int _trackGeneration;
    private bool _publishPending;
    private bool _disposed;
    private CancellationTokenSource? _artworkCancellation;

    private MacNowPlayingSession(NowPlayingKeys keys)
    {
        _keys = keys;
    }

    public event EventHandler? PlayPauseRequested;

    public event EventHandler? PlayRequested;

    public event EventHandler? PauseRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PreviousRequested;

    public event EventHandler? StopRequested;

    public event EventHandler<TimeSpan>? SeekRequested;

    public event EventHandler? QuitRequested
    {
        add { }
        remove { }
    }

    public static IMediaSession Create()
    {
        if (!MacMainThread.IsRunning || Volatile.Read(ref _current) is not null)
        {
            return new NullMediaSession();
        }

        NowPlayingKeys keys;
        try
        {
            IntPtr mediaPlayer = NativeLibrary.Load(MediaPlayerPath);
            _ = NativeLibrary.Load(AppKitPath);
            keys = NowPlayingKeys.Load(mediaPlayer);
            EnsureArtworkBlock();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new NullMediaSession();
        }

        var session = new MacNowPlayingSession(keys);
        Volatile.Write(ref _current, session);
        if (!MacMainThread.Invoke(session.RegisterCommands, TeardownTimeout))
        {
            Volatile.Write(ref _current, null);
            return new NullMediaSession();
        }

        return session;
    }

    public void UpdateTrack(Song? song, Uri? artworkUri)
    {
        int generation;
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource? artworkCancellation = null;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _song = song;
            _length = song?.Duration is > 0 ? TimeSpan.FromSeconds(song.Duration.Value) : null;
            SetPosition(TimeSpan.Zero);
            generation = ++_trackGeneration;
            previousCancellation = _artworkCancellation;
            if (song is not null && artworkUri is not null)
            {
                artworkCancellation = new CancellationTokenSource();
            }

            _artworkCancellation = artworkCancellation;
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        MacMainThread.Post(() =>
        {
            ObjC.Release(_artwork);
            _artwork = IntPtr.Zero;
        });
        SchedulePublish();

        if (artworkCancellation is not null)
        {
            _ = LoadArtworkAsync(artworkUri!, generation, artworkCancellation.Token);
        }
    }

    public void UpdatePlaybackState(PlaybackState state)
    {
        lock (_stateLock)
        {
            SetPosition(state == PlaybackState.Stopped ? TimeSpan.Zero : CurrentPosition());
            _playbackState = state;
        }

        SchedulePublish();
    }

    public void UpdateProgress(TimeSpan position, TimeSpan? duration)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = (position - CurrentPosition()).Duration() > ProgressTolerance;
            SetPosition(position);
            if (_song is not null && _length is null && duration > TimeSpan.Zero)
            {
                _length = duration;
                changed = true;
            }
        }

        if (changed)
        {
            SchedulePublish();
        }
    }

    public void NotifySeeked(TimeSpan position)
    {
        lock (_stateLock)
        {
            SetPosition(position);
        }

        SchedulePublish();
    }

    public void Dispose()
    {
        CancellationTokenSource? artworkCancellation;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            artworkCancellation = _artworkCancellation;
            _artworkCancellation = null;
        }

        artworkCancellation?.Cancel();
        artworkCancellation?.Dispose();
        _ = MacMainThread.Invoke(Teardown, TeardownTimeout);
        Volatile.Write(ref _current, null);
    }

    private void RegisterCommands()
    {
        _target = ObjC.Send(ObjC.Send(GetTargetClass(), "alloc"), "init");
        IntPtr commandCenter = ObjC.Send(ObjC.Class("MPRemoteCommandCenter"), "sharedCommandCenter");
        foreach (var (command, selector) in Commands)
        {
            IntPtr remoteCommand = ObjC.Send(commandCenter, command);
            ObjC.SendVoid(remoteCommand, "setEnabled:", true);
            _ = ObjC.Send(remoteCommand, "addTarget:action:", _target, ObjC.Sel(selector));
        }

        Publish();
    }

    private void Teardown()
    {
        IntPtr commandCenter = ObjC.Send(ObjC.Class("MPRemoteCommandCenter"), "sharedCommandCenter");
        foreach (var (command, _) in Commands)
        {
            IntPtr remoteCommand = ObjC.Send(commandCenter, command);
            ObjC.SendVoid(remoteCommand, "removeTarget:", _target);
            ObjC.SendVoid(remoteCommand, "setEnabled:", false);
        }

        IntPtr infoCenter = ObjC.Send(ObjC.Class("MPNowPlayingInfoCenter"), "defaultCenter");
        ObjC.SendVoid(infoCenter, "setNowPlayingInfo:", IntPtr.Zero);
        ObjC.SendVoid(infoCenter, "setPlaybackState:", PlaybackStateValue(PlaybackState.Stopped));

        ObjC.Release(_target);
        ObjC.Release(_artwork);
        _target = IntPtr.Zero;
        _artwork = IntPtr.Zero;
        lock (ArtworkImageLock)
        {
            ObjC.Release(_artworkImage);
            _artworkImage = IntPtr.Zero;
        }
    }

    private void SchedulePublish()
    {
        lock (_stateLock)
        {
            if (_disposed || _publishPending)
            {
                return;
            }

            _publishPending = true;
        }

        MacMainThread.Post(Publish);
    }

    private void Publish()
    {
        Song? song;
        TimeSpan? length;
        TimeSpan position;
        PlaybackState state;
        lock (_stateLock)
        {
            _publishPending = false;
            if (_disposed)
            {
                return;
            }

            song = _song;
            length = _length;
            position = CurrentPosition();
            state = _playbackState;
        }

        IntPtr infoCenter = ObjC.Send(ObjC.Class("MPNowPlayingInfoCenter"), "defaultCenter");
        IntPtr info = IntPtr.Zero;
        if (song is not null)
        {
            info = ObjC.Send(ObjC.Class("NSMutableDictionary"), "dictionary");
            SetValue(info, _keys.Title, ObjC.NSString(song.Title));
            if (!string.IsNullOrEmpty(song.Artist))
            {
                SetValue(info, _keys.Artist, ObjC.NSString(song.Artist));
            }

            if (!string.IsNullOrEmpty(song.Album))
            {
                SetValue(info, _keys.AlbumTitle, ObjC.NSString(song.Album));
            }

            if (length is { } duration)
            {
                SetValue(info, _keys.PlaybackDuration, ObjC.NSNumber(duration.TotalSeconds));
            }

            SetValue(info, _keys.ElapsedPlaybackTime, ObjC.NSNumber(position.TotalSeconds));
            SetValue(info, _keys.PlaybackRate, ObjC.NSNumber(state == PlaybackState.Playing ? 1.0 : 0.0));
            if (_artwork != IntPtr.Zero)
            {
                SetValue(info, _keys.Artwork, _artwork);
            }
        }

        ObjC.SendVoid(infoCenter, "setNowPlayingInfo:", info);
        ObjC.SendVoid(infoCenter, "setPlaybackState:", PlaybackStateValue(song is null ? PlaybackState.Stopped : state));
    }

    private async Task LoadArtworkAsync(Uri artworkUri, int generation, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await ArtworkClient.GetByteArrayAsync(artworkUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              or InvalidOperationException)
        {
            return;
        }

        MacMainThread.Post(() => ApplyArtwork(bytes, generation));
    }

    private void ApplyArtwork(byte[] bytes, int generation)
    {
        lock (_stateLock)
        {
            if (_disposed || generation != _trackGeneration)
            {
                return;
            }
        }

        IntPtr image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), "alloc"), "initWithData:", ObjC.NSData(bytes));
        if (image == IntPtr.Zero)
        {
            return;
        }

        CGSize size = ObjC.SendSize(image, "size");
        lock (ArtworkImageLock)
        {
            ObjC.Release(_artworkImage);
            _artworkImage = image;
        }

        ObjC.Release(_artwork);
        _artwork = ObjC.InitWithSize(
            ObjC.Send(ObjC.Class("MPMediaItemArtwork"), "alloc"),
            "initWithBoundsSize:requestHandler:",
            size,
            _artworkBlock);
        Publish();
    }

    private TimeSpan CurrentPosition()
    {
        if (_playbackState != PlaybackState.Playing)
        {
            return _position;
        }

        TimeSpan position = _position + Stopwatch.GetElapsedTime(_positionTimestamp);
        return _length is { } length && position > length ? length : position;
    }

    private void SetPosition(TimeSpan position)
    {
        _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _positionTimestamp = Stopwatch.GetTimestamp();
    }

    private static void SetValue(IntPtr dictionary, IntPtr key, IntPtr value) =>
        ObjC.SendVoid(dictionary, "setObject:forKey:", value, key);

    private static nuint PlaybackStateValue(PlaybackState state) => state switch
    {
        PlaybackState.Playing => 1,
        PlaybackState.Paused => 2,
        _ => 3
    };

    private static unsafe IntPtr GetTargetClass()
    {
        IntPtr targetClass = ObjC.Class(TargetClassName);
        if (targetClass != IntPtr.Zero)
        {
            return targetClass;
        }

        targetClass = ObjC.AllocateClassPair(ObjC.Class("NSObject"), TargetClassName, 0);
        AddHandler(targetClass, "handlePlay:", &HandlePlay);
        AddHandler(targetClass, "handlePause:", &HandlePause);
        AddHandler(targetClass, "handleTogglePlayPause:", &HandleTogglePlayPause);
        AddHandler(targetClass, "handleNextTrack:", &HandleNextTrack);
        AddHandler(targetClass, "handlePreviousTrack:", &HandlePreviousTrack);
        AddHandler(targetClass, "handleStop:", &HandleStop);
        AddHandler(targetClass, "handleChangePlaybackPosition:", &HandleChangePlaybackPosition);
        ObjC.RegisterClassPair(targetClass);
        return targetClass;
    }

    private static unsafe void AddHandler(
        IntPtr targetClass,
        string selector,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint> handler) =>
        _ = ObjC.AddMethod(targetClass, ObjC.Sel(selector), (IntPtr)handler, "q@:@");

    private static unsafe void EnsureArtworkBlock()
    {
        if (_artworkBlock != IntPtr.Zero)
        {
            return;
        }

        IntPtr libSystem = NativeLibrary.Load(LibSystemPath);
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8("@24@?0{CGSize=dd}8");

        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = NativeLibrary.GetExport(libSystem, "_NSConcreteGlobalBlock");
        block->Flags = BlockIsGlobal | BlockHasSignature;
        block->Invoke = (IntPtr)(delegate* unmanaged<IntPtr, CGSize, IntPtr>)&ProvideArtwork;
        block->Descriptor = descriptor;
        _artworkBlock = (IntPtr)block;
    }

    [UnmanagedCallersOnly]
    private static IntPtr ProvideArtwork(IntPtr block, CGSize size)
    {
        lock (ArtworkImageLock)
        {
            return ObjC.RetainAutorelease(_artworkImage);
        }
    }

    [UnmanagedCallersOnly]
    private static nint HandlePlay(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.PlayRequested);

    [UnmanagedCallersOnly]
    private static nint HandlePause(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.PauseRequested);

    [UnmanagedCallersOnly]
    private static nint HandleTogglePlayPause(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.PlayPauseRequested);

    [UnmanagedCallersOnly]
    private static nint HandleNextTrack(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.NextRequested);

    [UnmanagedCallersOnly]
    private static nint HandlePreviousTrack(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.PreviousRequested);

    [UnmanagedCallersOnly]
    private static nint HandleStop(IntPtr self, IntPtr selector, IntPtr commandEvent) =>
        Raise(static session => session.StopRequested);

    [UnmanagedCallersOnly]
    private static nint HandleChangePlaybackPosition(IntPtr self, IntPtr selector, IntPtr commandEvent)
    {
        MacNowPlayingSession? session = Volatile.Read(ref _current);
        if (session is null)
        {
            return HandlerFailed;
        }

        try
        {
            double seconds = ObjC.SendDouble(commandEvent, "positionTime");
            if (!double.IsFinite(seconds))
            {
                return HandlerFailed;
            }

            session.SeekRequested?.Invoke(session, TimeSpan.FromSeconds(Math.Max(0, seconds)));
            return HandlerSuccess;
        }
        catch (Exception)
        {
            return HandlerFailed;
        }
    }

    private static nint Raise(Func<MacNowPlayingSession, EventHandler?> selectHandler)
    {
        MacNowPlayingSession? session = Volatile.Read(ref _current);
        if (session is null)
        {
            return HandlerFailed;
        }

        try
        {
            selectHandler(session)?.Invoke(session, EventArgs.Empty);
            return HandlerSuccess;
        }
        catch (Exception)
        {
            return HandlerFailed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public BlockDescriptor* Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public IntPtr Signature;
    }

    private sealed record NowPlayingKeys(
        IntPtr Title,
        IntPtr Artist,
        IntPtr AlbumTitle,
        IntPtr PlaybackDuration,
        IntPtr Artwork,
        IntPtr ElapsedPlaybackTime,
        IntPtr PlaybackRate)
    {
        public static NowPlayingKeys Load(IntPtr mediaPlayer) => new(
            Read(mediaPlayer, "MPMediaItemPropertyTitle"),
            Read(mediaPlayer, "MPMediaItemPropertyArtist"),
            Read(mediaPlayer, "MPMediaItemPropertyAlbumTitle"),
            Read(mediaPlayer, "MPMediaItemPropertyPlaybackDuration"),
            Read(mediaPlayer, "MPMediaItemPropertyArtwork"),
            Read(mediaPlayer, "MPNowPlayingInfoPropertyElapsedPlaybackTime"),
            Read(mediaPlayer, "MPNowPlayingInfoPropertyPlaybackRate"));

        private static IntPtr Read(IntPtr library, string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }
}
