using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amber.Core.RemoteControl.Windows;

/// <summary>
/// Windows 截屏：GDI BitBlt 整个虚拟桌面（所有显示器）。锁屏、UAC 这些安全桌面截不到（返回 false）。
/// 以后换 Windows.Graphics.Capture（能只截游戏窗口）。进程要是 DPI 感知的（Avalonia 启动时已设置），否则截到的是缩放后的。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenSource : IScreenSource
{
    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _bits;
    private int _width;
    private int _height;

    public ScreenBounds Bounds => Native.VirtualDesktop();

    public bool TryCapture(ScreenFrame frame)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return false;
        if (!EnsureBitmap(b.Width, b.Height)) return false;
        // CAPTUREBLT 带上分层窗口（很多现代程序的弹窗）。
        if (!Native.BitBlt(_memoryDc, 0, 0, b.Width, b.Height, _screenDc, b.X, b.Y, Native.SRCCOPY | Native.CAPTUREBLT)) return false;
        Native.GdiFlush();
        frame.Resize(b.Width, b.Height);
        Marshal.Copy(_bits, frame.Pixels, 0, frame.Pixels.Length);
        // GDI 不填透明度，设成不透明。
        var px = frame.Pixels;
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return true;
    }

    public (int X, int Y)? CursorPosition() => Native.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    private bool EnsureBitmap(int width, int height)
    {
        if (_bitmap != 0 && width == _width && height == _height) return true;
        Release();
        _screenDc = Native.GetDC(0);
        if (_screenDc == 0) return false;
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
        var info = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 从上往下
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_screenDc, ref info, 0, out _bits, 0, 0);
        if (_bitmap == 0 || _memoryDc == 0) return false;
        Native.SelectObject(_memoryDc, _bitmap);
        _width = width;
        _height = height;
        return true;
    }

    private void Release()
    {
        if (_memoryDc != 0) Native.DeleteDC(_memoryDc);
        if (_bitmap != 0) Native.DeleteObject(_bitmap);
        if (_screenDc != 0) Native.ReleaseDC(0, _screenDc);
        _memoryDc = _bitmap = _screenDc = _bits = 0;
        _width = _height = 0;
    }

    public void Dispose() => Release();
}

/// <summary>
/// Windows 注入：SendInput。鼠标用整个虚拟桌面的绝对坐标（MOUSEEVENTF_VIRTUALDESK），键盘用扫描码（不受双方键盘布局差异影响）。
/// 安全桌面（UAC、锁屏、Ctrl+Alt+Del）Windows 本身不让注入。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SendInputSink : IInputSink
{
    public void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta)
    {
        var (ax, ay) = RemoteGeometry.ToAbsolute(Native.VirtualDesktop(), desktopX, desktopY);
        uint flags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        var data = 0;
        switch (action)
        {
            case RemoteMouseAction.Down:
                flags |= button switch { RemoteMouseButton.Left => Native.MOUSEEVENTF_LEFTDOWN, RemoteMouseButton.Right => Native.MOUSEEVENTF_RIGHTDOWN, RemoteMouseButton.Middle => Native.MOUSEEVENTF_MIDDLEDOWN, _ => 0u };
                break;
            case RemoteMouseAction.Up:
                flags |= button switch { RemoteMouseButton.Left => Native.MOUSEEVENTF_LEFTUP, RemoteMouseButton.Right => Native.MOUSEEVENTF_RIGHTUP, RemoteMouseButton.Middle => Native.MOUSEEVENTF_MIDDLEUP, _ => 0u };
                break;
            case RemoteMouseAction.Wheel:
                flags |= Native.MOUSEEVENTF_WHEEL;
                data = delta;
                break;
            case RemoteMouseAction.HWheel:
                flags |= Native.MOUSEEVENTF_HWHEEL;
                data = delta;
                break;
        }
        var input = new Native.INPUT { type = Native.INPUT_MOUSE };
        input.u.mi = new Native.MOUSEINPUT { dx = ax, dy = ay, mouseData = data, dwFlags = flags };
        Native.Send(input);
    }

    public void Key(ushort scanCode, bool up, bool extended)
    {
        var flags = Native.KEYEVENTF_SCANCODE | (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0);
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.u.ki = new Native.KEYBDINPUT { wScan = scanCode, dwFlags = flags };
        Native.Send(input);
    }

    public void Dispose() { }
}

/// <summary>
/// 全局热键（被控方随时按 Ctrl+Shift+F12 立即停止）。在自己的线程上注册并跑消息循环（RegisterHotKey 的消息发给注册它的线程）。
/// 热键被别的程序占了时 Registered 是 false（界面上照样有停止按钮）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x4A41; // "JA"
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private volatile bool _disposed;

    public bool Registered { get; private set; }

    /// <summary>按下时在热键线程上调用。</summary>
    public GlobalHotkey(Action pressed, uint modifiers = Native.MOD_CONTROL | Native.MOD_SHIFT, uint key = Native.VK_F12)
    {
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            // 先建出这个线程的消息队列，再告诉构造函数可以返回。
            Native.PeekMessage(out _, 0, 0, 0, 0);
            Registered = Native.RegisterHotKey(0, Id, modifiers | Native.MOD_NOREPEAT, key);
            _ready.Set();
            if (!Registered) return;
            try
            {
                while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
                    if (msg.message == Native.WM_HOTKEY && (int)msg.wParam == Id && !_disposed) pressed();
            }
            finally
            {
                Native.UnregisterHotKey(0, Id);
            }
        }) { IsBackground = true, Name = "Jay remote hotkey" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        _ready.Dispose();
    }
}

/// <summary>窗口点击穿透（红框窗口用）：加上 WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE。</summary>
[SupportedOSPlatform("windows")]
public static class WindowStyles
{
    public static void MakeClickThrough(nint hwnd)
    {
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    /// <summary>不抢焦点、不出现在任务栏（顶部小条用）。</summary>
    public static void MakeToolWindow(nint hwnd)
    {
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }
}

[SupportedOSPlatform("windows")]
internal static partial class Native
{
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    public const uint MOD_SHIFT = 0x0004, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    public const uint VK_F12 = 0x7B;
    public const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;

    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    public static ScreenBounds VirtualDesktop() => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static void Send(INPUT input)
    {
        Span<INPUT> inputs = [input];
        unsafe
        {
            fixed (INPUT* p = inputs) SendInput(1, p, Marshal.SizeOf<INPUT>());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // BITMAPINFO 的颜色表（32 位不用），留出位置。
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static unsafe partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint thread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
