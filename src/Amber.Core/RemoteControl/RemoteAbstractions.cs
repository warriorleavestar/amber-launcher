namespace Amber.Core.RemoteControl;

/// <summary>被共享的那块屏幕在桌面坐标里的位置（多显示器时是整个虚拟桌面，左上角可能是负数）。</summary>
public readonly record struct ScreenBounds(int X, int Y, int Width, int Height);

/// <summary>
/// 截屏来源。Windows 用 GDI（以后换 Windows.Graphics.Capture）；Mac/Linux 由合作者按这个接口补。
/// 只在内存里用，画面从不落盘、不进日志。
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>当前共享区域（每次截屏前可能变：插拔显示器、改分辨率）。</summary>
    ScreenBounds Bounds { get; }

    /// <summary>截一张 BGRA 画面到 frame（尺寸不对时 frame 自己重新分配）。失败返回 false（比如锁屏时安全桌面截不到）。</summary>
    bool TryCapture(ScreenFrame frame);

    /// <summary>鼠标指针在桌面坐标里的位置；拿不到时 null。</summary>
    (int X, int Y)? CursorPosition();
}

/// <summary>
/// 注入鼠标键盘。Windows 用 SendInput；坐标是桌面坐标（和 ScreenBounds 同一套）。
/// </summary>
public interface IInputSink : IDisposable
{
    void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta);
    void Key(ushort scanCode, bool up, bool extended);
}

/// <summary>画面块的编码。App 里用 Skia 的 JPEG；Core 自带一个无损的（测试和没有图像库时用）。</summary>
public interface ITileCodec
{
    /// <summary>编码的编号（写在 Accept 里，两边对不上就断开）。</summary>
    byte Id { get; }

    /// <summary>把一块 BGRA 像素编码。quality 0–100（无损编码忽略）。</summary>
    byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality);

    /// <summary>解码到目标 BGRA 缓冲（按 stride 写 width×height）。数据不对返回 false。</summary>
    bool Decode(ReadOnlySpan<byte> data, Span<byte> bgra, int width, int height, int stride);
}

/// <summary>谁在请求、请求什么（同意弹窗显示用）。名字和指纹来自握手认出来的成员 / 敲门时证明过的房主身份。</summary>
public sealed record RemoteConsentRequest(string Name, string Fingerprint);

/// <summary>被控方的决定。ViewOnly：只让看，不让控制。</summary>
public sealed record RemoteConsent(bool Accepted, bool ViewOnly)
{
    public static readonly RemoteConsent Denied = new(false, true);
}

/// <summary>
/// 同意策略：被控方怎么决定接不接受一次远程控制。可以换（以后可能加“信任此人”）；
/// 默认实现是每次都问（<see cref="AskEveryTimePolicy"/>）。
/// </summary>
public interface IRemoteConsentPolicy
{
    Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct);
}

/// <summary>每次都弹窗问；30 秒不点、取消或出错都算拒绝。</summary>
public sealed class AskEveryTimePolicy(Func<RemoteConsentRequest, CancellationToken, Task<RemoteConsent>> prompt, TimeSpan? timeout = null) : IRemoteConsentPolicy
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    public async Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var task = prompt(request, cts.Token);
            return await task.WaitAsync(cts.Token) ?? RemoteConsent.Denied;
        }
        catch (OperationCanceledException)
        {
            return RemoteConsent.Denied;
        }
    }
}

/// <summary>按当前系统选截屏和注入的实现。Mac/Linux 还没有（返回 null，界面上提示不支持）。</summary>
public static class RemotePlatform
{
    public static bool Supported => OperatingSystem.IsWindows();

    public static IScreenSource? CreateScreen() => OperatingSystem.IsWindows() ? new Windows.GdiScreenSource() : null;

    public static IInputSink? CreateInput() => OperatingSystem.IsWindows() ? new Windows.SendInputSink() : null;
}
