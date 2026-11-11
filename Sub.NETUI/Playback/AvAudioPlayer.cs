using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sub.NETUI.Platform.MacOS;
using Sub.NETUI.Subsonic;

namespace Sub.NETUI.Playback;

/// <summary>
/// Plays streams with AVFoundation's AVPlayer, so macOS needs no external player. All Objective-C calls run on the
/// main dispatch queue (see <see cref="MacMainThread"/>).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class AvAudioPlayer : IAudioPlayer
{
    private const string AVFoundationPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string ObserverClassName = "SubNETUIPlayerObserver";
    private const nint StatusReadyToPlay = 1;
    private const nint StatusFailed = 2;
    private const string TranscodeFormat = "mp3";

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoadPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<IntPtr, AvAudioPlayer> Players = new();

    // Formats Core Audio decodes natively; anything else (Ogg Vorbis, Opus, WMA, ...) is transcoded by Navidrome.
    private static readonly HashSet<string> NativeSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "m4b", "mp4", "aac", "alac", "flac", "wav", "aif", "aiff", "aifc", "caf"
    };

    private static IntPtr _didPlayToEndNotification;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _itemLock = new();
    private IntPtr _player;
    private IntPtr _observer;
    private IntPtr _item;
    private bool _disposed;

    public event EventHandler? TrackEnded;

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public static bool TryInitialize()
    {
        if (!MacMainThread.IsRunning)
        {
            return false;
        }

        try
        {
            IntPtr avFoundation = NativeLibrary.Load(AVFoundationPath);
            _didPlayToEndNotification = Marshal.ReadIntPtr(
                NativeLibrary.GetExport(avFoundation, "AVPlayerItemDidPlayToEndTimeNotification"));
            return _didPlayToEndNotification != IntPtr.Zero;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public string? GetStreamFormat(Song song) =>
        song.Suffix is { } suffix && NativeSuffixes.Contains(suffix) ? null : TranscodeFormat;

    public async Task PlayAsync(
        Uri streamUri,
        TimeSpan startPosition = default,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsPlaying = false;
            IsPaused = false;
            await MacMainThread.InvokeAsync(() => LoadItem(streamUri));
            if (startPosition > TimeSpan.Zero)
            {
                await WaitUntilReadyAsync(cancellationToken);
                await MacMainThread.InvokeAsync(() => SeekOnMainThread(startPosition));
            }

            await MacMainThread.InvokeAsync(() => ObjC.Send(_player, "play"));
            IsPlaying = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        bool newPauseState = !IsPaused;
        await MacMainThread.InvokeAsync(() => ObjC.Send(_player, newPauseState ? "pause" : "play"));
        IsPaused = newPauseState;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        await MacMainThread.InvokeAsync(UnloadItem);
        IsPlaying = false;
        IsPaused = false;
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return;
        }

        await MacMainThread.InvokeAsync(() => SeekOnMainThread(position));
    }

    public async Task<(TimeSpan? Position, TimeSpan? Duration)> GetProgressAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsPlaying)
        {
            return (null, null);
        }

        return await MacMainThread.InvokeAsync<(TimeSpan?, TimeSpan?)>(() =>
        {
            IntPtr item = _item;
            if (_player == IntPtr.Zero || item == IntPtr.Zero)
            {
                return (null, null);
            }

            return (ObjC.SendTime(_player, "currentTime").ToTimeSpan(), ObjC.SendTime(item, "duration").ToTimeSpan());
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsPlaying = false;
        IsPaused = false;
        _ = MacMainThread.Invoke(Teardown, TeardownTimeout);
        _lifecycleGate.Dispose();
    }

    private void LoadItem(Uri streamUri)
    {
        EnsurePlayer();
        IntPtr url = ObjC.Send(ObjC.Class("NSURL"), "URLWithString:", ObjC.NSString(streamUri.AbsoluteUri));
        IntPtr item = url == IntPtr.Zero
            ? IntPtr.Zero
            : ObjC.Send(ObjC.Class("AVPlayerItem"), "playerItemWithURL:", url);
        if (item == IntPtr.Zero)
        {
            throw new InvalidOperationException("AVPlayer could not open the stream.");
        }

        ReplaceItem(ObjC.Send(item, "retain"));
    }

    private void UnloadItem()
    {
        if (_player != IntPtr.Zero)
        {
            ObjC.Send(_player, "pause");
        }

        ReplaceItem(IntPtr.Zero);
    }

    private void ReplaceItem(IntPtr item)
    {
        IntPtr previous;
        lock (_itemLock)
        {
            previous = _item;
            _item = item;
        }

        if (_player != IntPtr.Zero)
        {
            ObjC.SendVoid(_player, "replaceCurrentItemWithPlayerItem:", item);
        }

        ObjC.Release(previous);
    }

    private void SeekOnMainThread(TimeSpan position)
    {
        if (_player == IntPtr.Zero || _item == IntPtr.Zero)
        {
            return;
        }

        CMTime target = CMTime.FromSeconds(Math.Max(0, position.TotalSeconds));
        ObjC.SendVoid(_player, "seekToTime:toleranceBefore:toleranceAfter:", target, CMTime.Zero, CMTime.Zero);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoadTimeout);
        try
        {
            while (true)
            {
                nint status = await MacMainThread.InvokeAsync(() =>
                    _item == IntPtr.Zero ? StatusFailed : (nint)ObjC.Send(_item, "status"));
                if (status == StatusReadyToPlay)
                {
                    return;
                }

                if (status == StatusFailed)
                {
                    throw new InvalidOperationException("AVPlayer could not load the stream.");
                }

                await Task.Delay(LoadPollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The stream did not load in time.");
        }
    }

    private void EnsurePlayer()
    {
        if (_player != IntPtr.Zero)
        {
            return;
        }

        _player = ObjC.Send(ObjC.Send(ObjC.Class("AVPlayer"), "alloc"), "init");
        _observer = ObjC.Send(ObjC.Send(GetObserverClass(), "alloc"), "init");
        Players[_observer] = this;
        IntPtr notificationCenter = ObjC.Send(ObjC.Class("NSNotificationCenter"), "defaultCenter");
        ObjC.SendVoid(
            notificationCenter,
            "addObserver:selector:name:object:",
            _observer,
            ObjC.Sel("playerItemDidPlayToEnd:"),
            _didPlayToEndNotification,
            IntPtr.Zero);
    }

    private void Teardown()
    {
        if (_observer != IntPtr.Zero)
        {
            IntPtr notificationCenter = ObjC.Send(ObjC.Class("NSNotificationCenter"), "defaultCenter");
            ObjC.SendVoid(notificationCenter, "removeObserver:", _observer);
            Players.TryRemove(_observer, out _);
        }

        UnloadItem();
        ObjC.Release(_player);
        ObjC.Release(_observer);
        _player = IntPtr.Zero;
        _observer = IntPtr.Zero;
    }

    private void OnItemDidPlayToEnd(IntPtr item)
    {
        lock (_itemLock)
        {
            if (_disposed || item == IntPtr.Zero || item != _item)
            {
                return;
            }
        }

        IsPlaying = false;
        IsPaused = false;
        _ = Task.Run(() => TrackEnded?.Invoke(this, EventArgs.Empty));
    }

    private static unsafe IntPtr GetObserverClass()
    {
        IntPtr observerClass = ObjC.Class(ObserverClassName);
        if (observerClass != IntPtr.Zero)
        {
            return observerClass;
        }

        observerClass = ObjC.AllocateClassPair(ObjC.Class("NSObject"), ObserverClassName, 0);
        _ = ObjC.AddMethod(
            observerClass,
            ObjC.Sel("playerItemDidPlayToEnd:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PlayerItemDidPlayToEnd,
            "v@:@");
        ObjC.RegisterClassPair(observerClass);
        return observerClass;
    }

    [UnmanagedCallersOnly]
    private static void PlayerItemDidPlayToEnd(IntPtr self, IntPtr selector, IntPtr notification)
    {
        try
        {
            if (Players.TryGetValue(self, out AvAudioPlayer? player))
            {
                player.OnItemDidPlayToEnd(ObjC.Send(notification, "object"));
            }
        }
        catch (Exception)
        {
            // Exceptions must not cross into native code.
        }
    }
}
