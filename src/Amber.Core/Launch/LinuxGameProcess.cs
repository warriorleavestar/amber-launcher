using System.Runtime.InteropServices;

namespace Amber.Core.Launch;

/// <summary>怎么让游戏退出的结果。</summary>
public enum ExitRequest
{
    /// <summary>没发出去（比如窗口还没出来）：游戏还在正常运行。</summary>
    NotSent,
    /// <summary>像点窗口关闭按钮一样请求关闭，游戏会自己保存再退出。</summary>
    WindowClose,
    /// <summary>找不到能关的窗口，发了 SIGTERM：新版本会照常保存，老版本可能来不及存档。</summary>
    Signal,
}

/// <summary>
/// Linux 上让游戏正常退出：X11 下找 _NET_WM_PID 是游戏进程的窗口，发 WM_DELETE_WINDOW（和点关闭按钮一样）；
/// 找不到这样的窗口时（Wayland、没有图形会话、还在加载）发 SIGTERM（<see cref="UnixGameProcess"/>）。
/// </summary>
internal static unsafe class LinuxGameProcess
{
    private const int ClientMessage = 33;
    private const int Success = 0;
    private const nint AnyPropertyType = 0;

    internal static ExitRequest RequestExit(int processId)
    {
        if (TryCloseX11Windows(processId) is > 0) return ExitRequest.WindowClose;
        // Wayland 原生窗口、没有 X11、窗口还没出来或者老 LWJGL 没设 _NET_WM_PID：退而发 SIGTERM，JVM 会跑关闭钩子。
        return UnixGameProcess.RequestExit(processId) ? ExitRequest.Signal : ExitRequest.NotSent;
    }

    // ---- X11（用到才加载，没有 libX11 或者没有 DISPLAY 时返回 null）----

    private static nint _lib;

    private static bool Load()
    {
        if (_lib != 0) return true;
        return NativeLibrary.TryLoad("libX11.so.6", out _lib) || NativeLibrary.TryLoad("libX11.so", out _lib);
    }

    private static nint Fn(string name) => NativeLibrary.GetExport(_lib, name);

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XClientMessageEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public nuint Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public nint Display;
        [FieldOffset(32)] public nint Window;
        [FieldOffset(40)] public nint MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public nint Data0;
        [FieldOffset(64)] public nint Data1;
    }

    [UnmanagedCallersOnly]
    private static int IgnoreError(nint display, nint error) => 0;

    /// <summary>返回发了几个关闭请求；X11 用不了时返回 null。</summary>
    private static int? TryCloseX11Windows(int processId)
    {
        // X11 的 long 是 64 位才和 nint 对得上；Jay 的 Linux 版只有 x64 和 arm64。
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8 || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        try
        {
            if (!Load()) return null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException) { return null; }

        var open = (delegate* unmanaged<byte*, nint>)Fn("XOpenDisplay");
        var close = (delegate* unmanaged<nint, int>)Fn("XCloseDisplay");
        var root = (delegate* unmanaged<nint, nint>)Fn("XDefaultRootWindow");
        var intern = (delegate* unmanaged<nint, byte*, int, nint>)Fn("XInternAtom");
        var queryTree = (delegate* unmanaged<nint, nint, nint*, nint*, nint**, uint*, int>)Fn("XQueryTree");
        var getProperty = (delegate* unmanaged<nint, nint, nint, nint, nint, int, nint, nint*, int*, nuint*, nuint*, byte**, int>)Fn("XGetWindowProperty");
        var getProtocols = (delegate* unmanaged<nint, nint, nint**, int*, int>)Fn("XGetWMProtocols");
        var send = (delegate* unmanaged<nint, nint, int, nint, XClientMessageEvent*, int>)Fn("XSendEvent");
        var flush = (delegate* unmanaged<nint, int>)Fn("XFlush");
        var sync = (delegate* unmanaged<nint, int, int>)Fn("XSync");
        var free = (delegate* unmanaged<void*, int>)Fn("XFree");
        var setErrorHandler = (delegate* unmanaged<delegate* unmanaged<nint, nint, int>, nint>)Fn("XSetErrorHandler");

        var display = open(null);
        if (display == 0) return null;
        // 遍历时窗口随时可能关掉（BadWindow）。默认的出错处理会直接结束整个进程，所以这段时间换成忽略，结束后换回去。
        var previous = setErrorHandler(&IgnoreError);
        try
        {
            nint Atom(string name)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
                fixed (byte* p = bytes) return intern(display, p, 0);
            }
            var pidAtom = Atom("_NET_WM_PID");
            var protocolsAtom = Atom("WM_PROTOCOLS");
            var deleteAtom = Atom("WM_DELETE_WINDOW");

            int? WindowPid(nint window)
            {
                nint type;
                int format;
                nuint count, after;
                byte* data = null;
                if (getProperty(display, window, pidAtom, 0, 1, 0, AnyPropertyType, &type, &format, &count, &after, &data) != Success) return null;
                try
                {
                    // 格式 32 的属性在客户端里按 C long 存。
                    return data is not null && format == 32 && count >= 1 ? (int)*(nint*)data : null;
                }
                finally
                {
                    if (data is not null) free(data);
                }
            }

            bool AcceptsDelete(nint window)
            {
                nint* protocols = null;
                var n = 0;
                if (getProtocols(display, window, &protocols, &n) == 0 || protocols is null) return false;
                try
                {
                    for (var i = 0; i < n; i++)
                        if (protocols[i] == deleteAtom) return true;
                    return false;
                }
                finally { free(protocols); }
            }

            var sent = 0;
            // 窗口管理器会把游戏窗口套进自己的边框窗口里，所以往下找几层。
            void Walk(nint window, int depth)
            {
                if (WindowPid(window) == processId && AcceptsDelete(window))
                {
                    var ev = new XClientMessageEvent
                    {
                        Type = ClientMessage,
                        Window = window,
                        MessageType = protocolsAtom,
                        Format = 32,
                        Data0 = deleteAtom,
                        Data1 = 0, // CurrentTime
                    };
                    if (send(display, window, 0, 0, &ev) != 0) sent++;
                    return;
                }
                if (depth >= 4) return;
                nint r, parent;
                nint* children = null;
                uint n = 0;
                if (queryTree(display, window, &r, &parent, &children, &n) == 0 || children is null) return;
                try
                {
                    for (var i = 0; i < n; i++) Walk(children[i], depth + 1);
                }
                finally { free(children); }
            }

            Walk(root(display), 0);
            flush(display);
            sync(display, 0);
            return sent;
        }
        finally
        {
            setErrorHandler((delegate* unmanaged<nint, nint, int>)previous);
            close(display);
        }
    }
}
