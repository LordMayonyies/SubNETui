using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sub.NETUI.Platform.MacOS;

/// <summary>
/// MediaPlayer delivers remote commands on the main dispatch queue, so on macOS the main thread runs a
/// CFRunLoop while the application itself runs on a worker thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacMainThread
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private static IntPtr _mainQueue;
    private static volatile bool _isRunning;

    public static bool IsRunning => _isRunning;

    public static int Run(Func<Task<int>> entry)
    {
        IntPtr defaultMode;
        try
        {
            IntPtr coreFoundation = NativeLibrary.Load(CoreFoundation);
            IntPtr libSystem = NativeLibrary.Load(LibSystem);
            _mainQueue = NativeLibrary.GetExport(libSystem, "_dispatch_main_q");
            defaultMode = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFRunLoopDefaultMode"));
            IntPtr commonModes = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFRunLoopCommonModes"));

            // A run loop without sources returns at once; an idle timer keeps it waiting instead of spinning.
            IntPtr timer = CFRunLoopTimerCreate(
                IntPtr.Zero,
                CFAbsoluteTimeGetCurrent() + 1e9,
                1e9,
                0,
                0,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnIdleTimer,
                IntPtr.Zero);
            CFRunLoopAddTimer(CFRunLoopGetMain(), timer, commonModes);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return entry().GetAwaiter().GetResult();
        }

        _isRunning = true;
        Task<int> task = Task.Run(entry);
        _ = task.ContinueWith(_ => CFRunLoopStop(CFRunLoopGetMain()), TaskScheduler.Default);
        while (!task.IsCompleted)
        {
            _ = CFRunLoopRunInMode(defaultMode, 1.0, 0);
        }

        _isRunning = false;
        return task.GetAwaiter().GetResult();
    }

    public static void Post(Action action)
    {
        if (!_isRunning)
        {
            return;
        }

        GCHandle handle = GCHandle.Alloc(action);
        DispatchAsync(_mainQueue, GCHandle.ToIntPtr(handle), &Execute);
    }

    public static bool Invoke(Action action, TimeSpan timeout)
    {
        using var done = new ManualResetEventSlim();
        Post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });
        return _isRunning && done.Wait(timeout);
    }

    public static Task<T> InvokeAsync<T>(Func<T> function)
    {
        if (!_isRunning)
        {
            return Task.FromException<T>(new InvalidOperationException("The macOS main run loop is not running."));
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(function());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    public static Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    [UnmanagedCallersOnly]
    private static void Execute(IntPtr context)
    {
        GCHandle handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();
        IntPtr pool = ObjC.AutoreleasePoolPush();
        try
        {
            action();
        }
        catch (Exception)
        {
            // Exceptions must not cross into native code; media session errors are never fatal.
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnIdleTimer(IntPtr timer, IntPtr info)
    {
    }

    [DllImport(LibSystem, EntryPoint = "dispatch_async_f")]
    private static extern void DispatchAsync(
        IntPtr queue,
        IntPtr context,
        delegate* unmanaged<IntPtr, void> work);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport(CoreFoundation)]
    private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, byte returnAfterSourceHandled);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    private static extern double CFAbsoluteTimeGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopTimerCreate(
        IntPtr allocator,
        double fireDate,
        double interval,
        nuint flags,
        nint order,
        IntPtr callout,
        IntPtr context);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopAddTimer(IntPtr runLoop, IntPtr timer, IntPtr mode);
}
