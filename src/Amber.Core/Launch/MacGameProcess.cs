using System.Runtime.InteropServices;

namespace Amber.Core.Launch;

/// <summary>向 Mac 游戏发送与 Dock“退出”相同的正常退出请求。</summary>
internal static class MacGameProcess
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    static MacGameProcess() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    internal static bool RequestExit(int processId)
    {
        var pool = PushAutoreleasePool();
        try
        {
            var application = GetApplication(GetClass("NSRunningApplication"),
                GetSelector("runningApplicationWithProcessIdentifier:"), processId);
            return application != 0 && SendBool(application, GetSelector("terminate"));
        }
        finally
        {
            PopAutoreleasePool(pool);
        }
    }

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint GetApplication(nint receiver, nint selector, int processId);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern nint PushAutoreleasePool();

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void PopAutoreleasePool(nint pool);
}
