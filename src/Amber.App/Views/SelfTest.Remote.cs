using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.RemoteControl;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 远程控制的真实窗口自检（AMBER_SELFTEST_SCOPE=remote）：同一个进程里开房主的远程控制服务和一个成员，
/// 经回环和真的成员钥匙通道走一遍：同意弹窗 → 查看窗口收到真实截屏 → 红框和小条 → 远程移动鼠标 → 踢人后立刻断开。
/// 每一步截图到报告目录。会短暂移动本机鼠标（结束后移回原处）；不碰游戏目录，不联网。
/// </summary>
internal sealed class RemoteSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];

    private void Check(string name, bool ok, string detail = "")
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "：" + detail : "")}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (Dispatcher.UIThread.Invoke(condition)) return true;
            await Task.Delay(100);
        }
        return Dispatcher.UIThread.Invoke(condition);
    }

    private async Task ShotAsync(Window target, string name)
    {
        await Task.Delay(500);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new PixelSize((int)Math.Ceiling(target.Bounds.Width * target.RenderScaling), (int)Math.Ceiling(target.Bounds.Height * target.RenderScaling));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * target.RenderScaling, 96 * target.RenderScaling));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(reportDir, name + ".png"));
        });
    }

    /// <summary>真实桌面的一块（红框和小条是系统合成的，只能从屏幕上截）。只截顶部一条和左下角，不截整个桌面。</summary>
    private void DesktopCrop(string name, Func<ScreenBounds, PixelRect> crop)
    {
        using var source = RemotePlatform.CreateScreen();
        if (source is null) return;
        var frame = new ScreenFrame();
        if (!source.TryCapture(frame)) return;
        var rect = crop(source.Bounds).Intersect(new PixelRect(0, 0, frame.Width, frame.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var bitmap = new WriteableBitmap(new PixelSize(rect.Width, rect.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
            for (var y = 0; y < rect.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, (rect.Y + y) * frame.Stride + rect.X * 4, fb.Address + y * fb.RowBytes, rect.Width * 4);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
    }

    /// <summary>把写着这段文字的按钮滚到看得见的地方（截图用）。</summary>
    private async Task ScrollToButtonAsync(string text)
    {
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Content as string == text)?.BringIntoView();
        });
    }

    private sealed class DialogPolicy(RemoteSelfTest test) : IRemoteConsentPolicy
    {
        public async Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct)
        {
            var decided = new TaskCompletionSource<RemoteConsent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var d = new RemoteConsentDialog(request, AskEveryTimePolicy.DefaultTimeout);
                d.Decided += c => decided.TrySetResult(c);
                d.Show();
                return d;
            });
            await test.ShotAsync(dialog, "01-consent-dialog");
            test.Check("同意弹窗：默认焦点在“拒绝”上", Dispatcher.UIThread.Invoke(() => dialog.FocusManager?.GetFocusedElement() is Button { IsDefault: true }));
            await Dispatcher.UIThread.InvokeAsync(() => dialog.AcceptForTest(viewOnly: false));
            return await decided.Task;
        }
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        L.SetLanguage(L.Source);
        try
        {
            await PageButtonsAsync();
            await EndToEndAsync();
        }
        catch (Exception e)
        {
            Check("远程控制自检出错", false, e.ToString());
        }
        _report.Add("DONE");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        window.Close();
    }

    /// <summary>联机页上的按钮：房主的成员列表、加入方的房主条目。</summary>
    private async Task PageButtonsAsync()
    {
        var mp = main.Pages.OfType<MultiplayerViewModel>().Single();
        var (hostRoom, _, guestRoom, entry) = Rooms();
        main.CurrentPage = mp;
        mp.ShowRemoteLayoutForTest(hostRoom, "host", new MemberItem("Alice", entry.PublicKey, false, Confirmed: true));
        Check("房主：邀请房间里成员行有“远程控制”按钮且可用", await WaitAsync(() => mp.CanRemoteControl, 3));
        await ScrollToButtonAsync(L.T("multiplayer.remote.button"));
        await ShotAsync(window, "00-host-member-list");
        mp.ShowRemoteLayoutForTest(guestRoom, "join");
        await ScrollToButtonAsync(L.T("multiplayer.remote.control_host"));
        await ShotAsync(window, "00-guest-control-host");
        mp.ShowRemoteLayoutForTest(RoomCode.Create(25565), "join");
        Check("AMB- 房间：按钮置灰并提示需要邀请房间", await WaitAsync(() => !mp.CanRemoteControl && mp.RemoteUnavailableText == L.T("multiplayer.remote.invite_only"), 3));
        await ScrollToButtonAsync(L.T("multiplayer.remote.control_host"));
        await ShotAsync(window, "00-old-room-disabled");
        mp.ShowRemoteLayoutForTest(null, "");
    }

    private static (RoomCode Host, MemberTable Table, RoomCode Guest, MemberEntry Entry) Rooms()
    {
        var host = X25519KeyPair.Generate();
        var guest = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey.ToArray(), 25565, new TunnelSettings(TransportMode.High, false));
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var entry = table.Issue(guest.PublicKey, "Alice");
        return (hostRoom, table, (credentials with { Member = entry.Key }).ToRoom(), entry);
    }

    private async Task EndToEndAsync()
    {
        if (!RemotePlatform.Supported)
        {
            Check("这台电脑不支持远程控制（不是 Windows），跳过端到端", true);
            return;
        }
        var (hostRoom, table, guestRoom, entry) = Rooms();
        // 截图里的“对方屏幕”只放 Jay 自己的窗口：主窗口最大化并置顶，不把本机别的程序截进报告。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.WindowState = WindowState.Maximized;
            window.Topmost = true;
        });
        try
        {
            await EndToEndCoreAsync(hostRoom, table, guestRoom, entry);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.Topmost = false;
                window.WindowState = WindowState.Normal;
            });
        }
    }

    private async Task EndToEndCoreAsync(RoomCode hostRoom, MemberTable table, RoomCode guestRoom, MemberEntry entry)
    {
        var ui = new RemoteWindows();
        RemoteSharer? sharer = null;
        await using var hub = new RemoteHub(hostRoom, new RemoteHostOptions(new DialogPolicy(this), RemotePlatform.CreateScreen, RemotePlatform.CreateInput, new SkiaTileCodec()), 0);
        hub.SharingStarted += (s, _) =>
        {
            sharer = s;
            ui.ShowSharing(s);
        };
        var stream = await RemoteConnector.OpenAsync("127.0.0.1", hub.ListenPort, guestRoom, RemoteRole.Controller, null);
        var viewer = new RemoteViewer(stream, new SkiaTileCodec(), "Host", "");
        var accept = await viewer.StartAsync();
        Check("同意后控制方收到 Accept（允许控制）", accept is { Control: true }, accept is { } a ? $"{a.Width}×{a.Height}" : $"{viewer.DenyReason}");
        if (accept is null) return;
        Check("编码用 Skia JPEG", accept.Value.Codec == new SkiaTileCodec().Id);
        RemoteViewerWindow? viewerWindow = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            viewerWindow = new RemoteViewerWindow(viewer) { Width = 960, Height = 620 };
            viewerWindow.Show();
        });
        Check("查看窗口收到真实画面", await WaitAsync(() => viewer.BatchesReceived > 0, 15), $"{viewer.BatchesReceived} 批");
        var nonBlack = false;
        lock (viewer.FrameLock)
        {
            var px = viewer.Frame.Pixels;
            for (var i = 0; i < px.Length && !nonBlack; i += 4 * 997) nonBlack = px[i] > 8 || px[i + 1] > 8 || px[i + 2] > 8;
        }
        Check("画面不是全黑（截屏和解码正常）", nonBlack);
        await Task.Delay(1500);
        await ShotAsync(viewerWindow!, "02-viewer-window");

        Check("被控方：红框和小条出现", await WaitAsync(() => sharer is not null && Application.Current?.ApplicationLifetime is not null
            && WindowsOf<RemoteSharingBar>().Count == 1 && RemoteSharingOverlay.FrameCount >= 4, 5), $"{RemoteSharingOverlay.FrameCount} 条边");
        var bar = WindowsOf<RemoteSharingBar>().FirstOrDefault();
        if (bar is not null) await ShotAsync(bar, "03-sharing-bar");
        await Task.Delay(500);
        DesktopCrop("04-desktop-top-with-bar", b => new PixelRect(0, 0, b.Width, Math.Min(b.Height, 140)));
        DesktopCrop("05-desktop-corner-red-frame", b => new PixelRect(0, Math.Max(0, b.Height - 240), Math.Min(b.Width, 360), Math.Min(b.Height, 240)));

        // 远程移动鼠标：控制方发画面坐标，被控方注入后真的指针跟着动。移完放回原处。
        using var screen = RemotePlatform.CreateScreen()!;
        var original = screen.CursorPosition();
        var (fw, fh) = (accept.Value.Width, accept.Value.Height);
        var target = (X: fw / 3, Y: fh / 3);
        await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, target.X, target.Y, 0));
        var expected = RemoteGeometry.FrameToDesktop(screen.Bounds, fw, fh, target.X, target.Y);
        var moved = await WaitAsync(() => screen.CursorPosition() is { } p && Math.Abs(p.X - expected.X) <= 3 && Math.Abs(p.Y - expected.Y) <= 3, 5);
        Check("远程移动鼠标：本机指针到了对应位置", moved, $"期望 {expected}，实际 {screen.CursorPosition()}");
        Check("被控方注入计数", sharer!.InputsInjected >= 1, $"{sharer.InputsInjected}");
        if (original is { } o) RemotePlatform.CreateInput()?.Mouse(RemoteMouseAction.Move, RemoteMouseButton.None, o.X, o.Y, 0);

        // 收回控制：控制方的状态跟着变，再发输入被丢弃。
        await sharer.SetControlAsync(false);
        Check("收回控制：控制方显示只能看", await WaitAsync(() => !viewer.ControlGranted, 5));
        if (bar is not null) await ShotAsync(bar, "06-sharing-bar-revoked");
        Check("收回控制后控制方不再发输入", !await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 1, 1, 0)));
        await ShotAsync(viewerWindow!, "07-viewer-view-only");

        // 踢人：钥匙作废，两边马上断开，红框收起。
        table.Kick(entry, null);
        Check("被踢后会话立即结束", await WaitAsync(() => viewer.HasEnded && sharer.HasEnded, 5));
        Check("被踢后红框和小条收起", await WaitAsync(() => WindowsOf<RemoteSharingBar>().Count == 0 && RemoteSharingOverlay.FrameCount == 0, 5));
        await ShotAsync(viewerWindow!, "08-viewer-ended");
        await Dispatcher.UIThread.InvokeAsync(() => viewerWindow!.Close());
    }

    private static List<T> WindowsOf<T>() where T : Window => Dispatcher.UIThread.Invoke(() =>
        (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows.OfType<T>().Where(w => w.IsVisible).ToList() ?? []);
}
