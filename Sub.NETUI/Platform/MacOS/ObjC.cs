using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sub.NETUI.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CGSize(double Width, double Height);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CMTime(long Value, int Timescale, uint Flags, long Epoch)
{
    private const uint Valid = 1;
    private const uint NotNumeric = 0x1C;

    public static CMTime Zero => new(0, 1, Valid, 0);

    public static CMTime FromSeconds(double seconds) => new((long)Math.Round(seconds * 1000), 1000, Valid, 0);

    public TimeSpan? ToTimeSpan() =>
        (Flags & Valid) != 0 && (Flags & NotNumeric) == 0 && Timescale > 0
            ? TimeSpan.FromSeconds((double)Value / Timescale)
            : null;
}

[SupportedOSPlatform("macos")]
internal static class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    public static IntPtr Class(string name) => GetClass(name);

    public static IntPtr Sel(string name) => RegisterName(name);

    public static IntPtr Send(IntPtr receiver, string selector) => MsgSend(receiver, Sel(selector));

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) =>
        MsgSend(receiver, Sel(selector), argument);

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr first, IntPtr second) =>
        MsgSend(receiver, Sel(selector), first, second);

    public static void SendVoid(IntPtr receiver, string selector, IntPtr argument) =>
        MsgSendVoid(receiver, Sel(selector), argument);

    public static void SendVoid(IntPtr receiver, string selector, IntPtr first, IntPtr second) =>
        MsgSendVoid(receiver, Sel(selector), first, second);

    public static void SendVoid(IntPtr receiver, string selector, nuint argument) =>
        MsgSendVoid(receiver, Sel(selector), argument);

    public static void SendVoid(IntPtr receiver, string selector, bool argument) =>
        MsgSendVoid(receiver, Sel(selector), argument ? (byte)1 : (byte)0);

    public static void SendVoid(IntPtr receiver, string selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d) =>
        MsgSendVoid(receiver, Sel(selector), a, b, c, d);

    public static void SendVoid(IntPtr receiver, string selector, CMTime time, CMTime before, CMTime after) =>
        MsgSendVoid(receiver, Sel(selector), time, before, after);

    public static CMTime SendTime(IntPtr receiver, string selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MsgSendTimeStret(receiver, Sel(selector))
            : MsgSendTime(receiver, Sel(selector));

    public static double SendDouble(IntPtr receiver, string selector) => MsgSendDouble(receiver, Sel(selector));

    public static CGSize SendSize(IntPtr receiver, string selector) => MsgSendSize(receiver, Sel(selector));

    public static IntPtr NSString(string value) =>
        MsgSendString(Class("NSString"), Sel("stringWithUTF8String:"), value);

    public static IntPtr NSNumber(double value) => MsgSend(Class("NSNumber"), Sel("numberWithDouble:"), value);

    public static IntPtr NSData(ReadOnlySpan<byte> bytes)
    {
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                return MsgSend(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)pointer, (nuint)bytes.Length);
            }
        }
    }

    public static IntPtr InitWithSize(IntPtr receiver, string selector, CGSize size, IntPtr argument) =>
        MsgSend(receiver, Sel(selector), size, argument);

    public static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            Send(instance, "release");
        }
    }

    public static IntPtr RetainAutorelease(IntPtr instance) =>
        instance == IntPtr.Zero ? IntPtr.Zero : Send(Send(instance, "retain"), "autorelease");

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC, EntryPoint = "objc_allocateClassPair")]
    public static extern IntPtr AllocateClassPair(
        IntPtr superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nuint extraBytes);

    [DllImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    public static extern void RegisterClassPair(IntPtr cls);

    [DllImport(LibObjC, EntryPoint = "class_addMethod")]
    public static extern byte AddMethod(
        IntPtr cls,
        IntPtr selector,
        IntPtr implementation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    public static extern IntPtr AutoreleasePoolPush();

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    public static extern void AutoreleasePoolPop(IntPtr pool);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr first, nuint second);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, CGSize size, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendString(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, nuint argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, byte argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, CMTime time, CMTime before, CMTime after);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern CMTime MsgSendTime(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern CMTime MsgSendTimeStret(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern CGSize MsgSendSize(IntPtr receiver, IntPtr selector);
}
