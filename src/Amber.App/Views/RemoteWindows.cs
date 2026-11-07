using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.RemoteControl;
using Amber.Core.RemoteControl.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>远程控制的窗口：同意弹窗、查看窗口、被控时的红框和小条。</summary>
public sealed class RemoteWindows : IRemoteUi
{
    public async Task<RemoteConsent> AskConsentAsync(RemoteConsentRequest request, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<RemoteConsent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new RemoteConsentDialog(request, AskEveryTimePolicy.DefaultTimeout);
            dialog.Decided += c => decision.TrySetResult(c);
            ct.Register(() => Dispatcher.UIThread.Post(() =>
            {
                decision.TrySetResult(RemoteConsent.Denied);
                dialog.Close();
            }));
            dialog.Show();
            dialog.Activate();
        });
        return await decision.Task;
    }

    public void ShowViewer(RemoteViewer viewer) => Dispatcher.UIThread.Post(() => new RemoteViewerWindow(viewer).Show());

    public void ShowSharing(RemoteSharer sharer) => Dispatcher.UIThread.Post(() => RemoteSharingOverlay.Show(sharer));
}

/// <summary>
/// 被控方每次都要点的同意窗：对方名字和身份指纹、风险提示、“只看不控”。默认按钮是“拒绝”，倒计时到 0 自动拒绝，关窗口也算拒绝。
/// </summary>
public sealed class RemoteConsentDialog : Window
{
    private readonly DispatcherTimer _timer;
    private bool _decided;

    public event Action<RemoteConsent>? Decided;

    public RemoteConsentDialog(RemoteConsentRequest request, TimeSpan timeout)
    {
        Title = L.T("remote.consent.window_title");
        SizeToContent = SizeToContent.Height;
        Width = 480;
        MaxWidth = 560;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var name = request.Name.Length > 0 ? request.Name : L.T("multiplayer.peers.player");
        var viewOnly = new CheckBox { Content = L.T("remote.consent.view_only") };
        var countdown = new TextBlock { Classes = { "hint" }, FontSize = 12 };
        var deny = new Button { Content = L.T("remote.consent.deny"), Classes = { "primary" }, IsDefault = true, IsCancel = true, MinWidth = 96 };
        var allow = new Button { Content = L.T("remote.consent.allow"), Classes = { "secondary" }, MinWidth = 96 };
        deny.Click += (_, _) => Decide(RemoteConsent.Denied);
        allow.Click += (_, _) => Decide(new RemoteConsent(true, viewOnly.IsChecked == true));
        var warning = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x2B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new TextBlock { Text = L.T("remote.consent.warning"), TextWrapping = TextWrapping.Wrap },
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = L.F("remote.consent.title", name), Classes = { "h2" }, Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = L.F("remote.consent.fingerprint", request.Fingerprint.Length > 0 ? request.Fingerprint : "-"), FontFamily = new FontFamily("Consolas,monospace"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = L.T("remote.consent.scope"), TextWrapping = TextWrapping.Wrap },
                    warning,
                    viewOnly,
                    countdown,
                    new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8, Children = { allow, deny } },
                },
            },
        };
        var deadline = DateTime.UtcNow + timeout;
        void Tick()
        {
            var left = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
            if (left <= 0) Decide(RemoteConsent.Denied);
            else countdown.Text = L.F("remote.consent.countdown", left);
        }
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick());
        Tick();
        _timer.Start();
        Opened += (_, _) => deny.Focus();
        Closed += (_, _) => Decide(RemoteConsent.Denied);
    }

    /// <summary>自检用：替用户点“同意”。</summary>
    internal void AcceptForTest(bool viewOnly) => Decide(new RemoteConsent(true, viewOnly));

    private void Decide(RemoteConsent consent)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();
        Decided?.Invoke(consent);
        Close();
    }
}

/// <summary>
/// 查看窗口：可以缩放，画面按比例居中。对方允许控制时，点一下画面后鼠标键盘发过去（扫描码）；只看时什么都不发。
/// 关窗口就结束这次远程控制。
/// </summary>
public sealed class RemoteViewerWindow : Window
{
    private readonly RemoteViewer _viewer;
    private readonly RemoteSurface _surface;
    private readonly TextBlock _state;

    public RemoteViewerWindow(RemoteViewer viewer)
    {
        _viewer = viewer;
        Title = L.F("remote.viewer.window_title", viewer.PeerName);
        Width = 1100;
        Height = 720;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _surface = new RemoteSurface(viewer);
        _state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var stop = new Button { Content = L.T("remote.viewer.stop"), Classes = { "danger" } };
        stop.Click += (_, _) => Close();
        var header = new Border
        {
            Padding = new Thickness(12, 8),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new WrapPanel
                            {
                                ItemSpacing = 12,
                                Children =
                                {
                                    new TextBlock { Text = viewer.PeerName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                                    new TextBlock { Text = viewer.PeerFingerprint, FontFamily = new FontFamily("Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center, Classes = { "hint" } },
                                    _state,
                                },
                            },
                            new TextBlock { Text = L.T("remote.viewer.hint"), Classes = { "hint" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                    new ContentControl { Content = stop, [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) },
                },
            },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new Border { Background = Brushes.Black, Child = _surface });
        Content = dock;
        UpdateState();
        viewer.ControlChanged += _ => Dispatcher.UIThread.Post(UpdateState);
        viewer.Ended += _ => Dispatcher.UIThread.Post(() =>
        {
            UpdateState();
            _surface.ReleaseAll();
        });
        Closed += (_, _) => _ = viewer.StopAsync();
        Opened += (_, _) => _surface.Focus();
    }

    private void UpdateState()
    {
        _state.Text = L.T(_viewer.HasEnded ? "remote.viewer.ended" : _viewer.ControlGranted ? "remote.viewer.can_control" : "remote.viewer.view_only");
        if (_viewer.ControlGranted && !_viewer.HasEnded) _state.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x48, 0x2B));
        else _state.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>测试 / 自检用：画面区域。</summary>
    internal RemoteSurface Surface => _surface;
}

/// <summary>显示远程画面并把键鼠发过去的控件。</summary>
public sealed class RemoteSurface : Control
{
    private readonly RemoteViewer _viewer;
    private WriteableBitmap? _bitmap;
    private int _pending;
    private readonly HashSet<RemoteKey> _keysDown = [];

    public RemoteSurface(RemoteViewer viewer)
    {
        _viewer = viewer;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        viewer.FrameChanged += ScheduleCopy;
        viewer.CursorChanged += () => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        ScheduleCopy();
    }

    /// <summary>画面变了：合并到界面线程上拷一次（读线程只置标记）。</summary>
    private void ScheduleCopy()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }

    private void CopyFrame()
    {
        Volatile.Write(ref _pending, 0);
        lock (_viewer.FrameLock)
        {
            var frame = _viewer.Frame;
            if (frame.Width == 0 || frame.Height == 0) return;
            if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var fb = _bitmap.Lock();
            for (var y = 0; y < frame.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * frame.Stride, fb.Address + y * fb.RowBytes, frame.Width * 4);
        }
        InvalidateVisual();
    }

    private Rect ImageRect()
    {
        if (_bitmap is null) return default;
        var (fw, fh) = (_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var scale = Math.Min(Bounds.Width / fw, Bounds.Height / fh);
        var size = new Size(fw * scale, fh * scale);
        return new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is null)
        {
            var text = new FormattedText(L.T("remote.viewer.waiting_frame"), L.Culture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            return;
        }
        var dest = ImageRect();
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), dest);
        // 对方的鼠标指针（截屏里没有）。
        var (cx, cy, visible) = _viewer.Cursor;
        if (visible)
        {
            var scale = dest.Width / _bitmap.PixelSize.Width;
            var p = new Point(dest.X + cx * scale, dest.Y + cy * scale);
            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                g.BeginFigure(p, true);
                g.LineTo(p + new Point(0, 16));
                g.LineTo(p + new Point(4.5, 12));
                g.LineTo(p + new Point(11, 11));
                g.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), arrow);
        }
    }

    private (int X, int Y)? FramePoint(PointerEventArgs e)
    {
        if (_bitmap is null) return null;
        var p = e.GetPosition(this);
        return RemoteGeometry.ViewToFrame(Bounds.Width, Bounds.Height, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height, p.X, p.Y);
    }

    private (int X, int Y) _last;

    private void SendMouse(RemoteMouseAction action, RemoteMouseButton button, (int X, int Y)? at, int delta = 0)
    {
        if (!_viewer.ControlGranted) return;
        if (at is { } p) _last = p;
        else if (action == RemoteMouseAction.Move) return;
        _ = _viewer.SendMouseAsync(new RemoteMouse(action, button, _last.X, _last.Y, delta));
    }

    private static RemoteMouseButton ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
        _ => RemoteMouseButton.None,
    };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SendMouse(RemoteMouseAction.Move, RemoteMouseButton.None, FramePoint(e));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var button = ButtonOf(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == RemoteMouseButton.None || FramePoint(e) is not { } p) return;
        e.Pointer.Capture(this);
        SendMouse(RemoteMouseAction.Down, button, p);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var button = ButtonOf(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == RemoteMouseButton.None) return;
        SendMouse(RemoteMouseAction.Up, button, FramePoint(e));
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = FramePoint(e);
        if (e.Delta.Y != 0) SendMouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, at, (int)Math.Round(e.Delta.Y * 120));
        if (e.Delta.X != 0) SendMouse(RemoteMouseAction.HWheel, RemoteMouseButton.None, at, (int)Math.Round(-e.Delta.X * 120));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (RemoteKeyMap.ScanCode(e.PhysicalKey) is not { } k) return;
        e.Handled = true;
        if (!_viewer.ControlGranted) return;
        var key = new RemoteKey(k.Scan, false, k.Extended);
        _keysDown.Add(key with { Up = true });
        _ = _viewer.SendKeyAsync(key);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (RemoteKeyMap.ScanCode(e.PhysicalKey) is not { } k) return;
        e.Handled = true;
        var up = new RemoteKey(k.Scan, true, k.Extended);
        if (_keysDown.Remove(up) && _viewer.ControlGranted) _ = _viewer.SendKeyAsync(up);
    }

    /// <summary>离开窗口时松开还按着的键（不然对方那边一直按着）。</summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAll();
    }

    internal void ReleaseAll()
    {
        foreach (var key in _keysDown) _ = _viewer.SendKeyAsync(key);
        _keysDown.Clear();
    }
}

/// <summary>Avalonia 的物理键 → PC 扫描码（Set 1）。按物理位置发，不受两边键盘布局和输入法影响。</summary>
public static class RemoteKeyMap
{
    public static (ushort Scan, bool Extended)? ScanCode(PhysicalKey key) => key switch
    {
        PhysicalKey.Escape => (0x01, false),
        PhysicalKey.Digit1 => (0x02, false), PhysicalKey.Digit2 => (0x03, false), PhysicalKey.Digit3 => (0x04, false), PhysicalKey.Digit4 => (0x05, false),
        PhysicalKey.Digit5 => (0x06, false), PhysicalKey.Digit6 => (0x07, false), PhysicalKey.Digit7 => (0x08, false), PhysicalKey.Digit8 => (0x09, false),
        PhysicalKey.Digit9 => (0x0A, false), PhysicalKey.Digit0 => (0x0B, false), PhysicalKey.Minus => (0x0C, false), PhysicalKey.Equal => (0x0D, false),
        PhysicalKey.Backspace => (0x0E, false), PhysicalKey.Tab => (0x0F, false),
        PhysicalKey.Q => (0x10, false), PhysicalKey.W => (0x11, false), PhysicalKey.E => (0x12, false), PhysicalKey.R => (0x13, false), PhysicalKey.T => (0x14, false),
        PhysicalKey.Y => (0x15, false), PhysicalKey.U => (0x16, false), PhysicalKey.I => (0x17, false), PhysicalKey.O => (0x18, false), PhysicalKey.P => (0x19, false),
        PhysicalKey.BracketLeft => (0x1A, false), PhysicalKey.BracketRight => (0x1B, false), PhysicalKey.Enter => (0x1C, false), PhysicalKey.ControlLeft => (0x1D, false),
        PhysicalKey.A => (0x1E, false), PhysicalKey.S => (0x1F, false), PhysicalKey.D => (0x20, false), PhysicalKey.F => (0x21, false), PhysicalKey.G => (0x22, false),
        PhysicalKey.H => (0x23, false), PhysicalKey.J => (0x24, false), PhysicalKey.K => (0x25, false), PhysicalKey.L => (0x26, false),
        PhysicalKey.Semicolon => (0x27, false), PhysicalKey.Quote => (0x28, false), PhysicalKey.Backquote => (0x29, false), PhysicalKey.ShiftLeft => (0x2A, false),
        PhysicalKey.Backslash => (0x2B, false),
        PhysicalKey.Z => (0x2C, false), PhysicalKey.X => (0x2D, false), PhysicalKey.C => (0x2E, false), PhysicalKey.V => (0x2F, false), PhysicalKey.B => (0x30, false),
        PhysicalKey.N => (0x31, false), PhysicalKey.M => (0x32, false), PhysicalKey.Comma => (0x33, false), PhysicalKey.Period => (0x34, false), PhysicalKey.Slash => (0x35, false),
        PhysicalKey.ShiftRight => (0x36, false), PhysicalKey.NumPadMultiply => (0x37, false), PhysicalKey.AltLeft => (0x38, false), PhysicalKey.Space => (0x39, false),
        PhysicalKey.CapsLock => (0x3A, false),
        PhysicalKey.F1 => (0x3B, false), PhysicalKey.F2 => (0x3C, false), PhysicalKey.F3 => (0x3D, false), PhysicalKey.F4 => (0x3E, false), PhysicalKey.F5 => (0x3F, false),
        PhysicalKey.F6 => (0x40, false), PhysicalKey.F7 => (0x41, false), PhysicalKey.F8 => (0x42, false), PhysicalKey.F9 => (0x43, false), PhysicalKey.F10 => (0x44, false),
        PhysicalKey.NumLock => (0x45, false), PhysicalKey.ScrollLock => (0x46, false),
        PhysicalKey.NumPad7 => (0x47, false), PhysicalKey.NumPad8 => (0x48, false), PhysicalKey.NumPad9 => (0x49, false), PhysicalKey.NumPadSubtract => (0x4A, false),
        PhysicalKey.NumPad4 => (0x4B, false), PhysicalKey.NumPad5 => (0x4C, false), PhysicalKey.NumPad6 => (0x4D, false), PhysicalKey.NumPadAdd => (0x4E, false),
        PhysicalKey.NumPad1 => (0x4F, false), PhysicalKey.NumPad2 => (0x50, false), PhysicalKey.NumPad3 => (0x51, false), PhysicalKey.NumPad0 => (0x52, false),
        PhysicalKey.NumPadDecimal => (0x53, false), PhysicalKey.IntlBackslash => (0x56, false), PhysicalKey.F11 => (0x57, false), PhysicalKey.F12 => (0x58, false),
        PhysicalKey.NumPadEnter => (0x1C, true), PhysicalKey.ControlRight => (0x1D, true), PhysicalKey.NumPadDivide => (0x35, true), PhysicalKey.PrintScreen => (0x37, true),
        PhysicalKey.AltRight => (0x38, true), PhysicalKey.Home => (0x47, true), PhysicalKey.ArrowUp => (0x48, true), PhysicalKey.PageUp => (0x49, true),
        PhysicalKey.ArrowLeft => (0x4B, true), PhysicalKey.ArrowRight => (0x4D, true), PhysicalKey.End => (0x4F, true), PhysicalKey.ArrowDown => (0x50, true),
        PhysicalKey.PageDown => (0x51, true), PhysicalKey.Insert => (0x52, true), PhysicalKey.Delete => (0x53, true),
        PhysicalKey.MetaLeft => (0x5B, true), PhysicalKey.MetaRight => (0x5C, true), PhysicalKey.ContextMenu => (0x5D, true),
        _ => null,
    };
}

/// <summary>
/// 被控期间的提示：每块屏幕边缘一圈红框（置顶、点击穿透），主屏顶部一个小条“正在被 XXX 控制 [收回控制] [停止]”，
/// 全局热键 Ctrl+Shift+F12 立即停止。会话结束时全部收起。
/// </summary>
public static class RemoteSharingOverlay
{
    public const double FrameThickness = 4;

    /// <summary>自检用：现在屏幕上有几条红框边。</summary>
    internal static int FrameCount { get; private set; }

    public static void Show(RemoteSharer sharer)
    {
        var windows = new List<Window>();
        IDisposable? frame = null;
        GlobalHotkey? hotkey = null;
        var bar = new RemoteSharingBar(sharer);
        windows.Add(bar);
        var closed = false;
        void CloseAll()
        {
            if (closed) return;
            closed = true;
            hotkey?.Dispose();
            frame?.Dispose();
            FrameCount = 0;
            foreach (var w in windows) w.Close();
        }
        sharer.Ended += _ => Dispatcher.UIThread.Post(CloseAll);
        if (OperatingSystem.IsWindows())
            hotkey = new GlobalHotkey(() => _ = sharer.StopAsync(RemoteStopReason.StoppedByUser));
        bar.HotkeyAvailable = hotkey?.Registered == true;
        // 红框（Windows 用系统原生窗口；其他系统以后补）。小条放在红框里面、不重叠。
        if (OperatingSystem.IsWindows())
        {
            var rects = new List<(int, int, int, int)>();
            foreach (var screen in bar.Screens.All)
            {
                var b = screen.Bounds;
                var t = (int)Math.Ceiling(FrameThickness * screen.Scaling);
                rects.AddRange([(b.X, b.Y, b.Width, t), (b.X, b.Bottom - t, b.Width, t), (b.X, b.Y, t, b.Height), (b.Right - t, b.Y, t, b.Height)]);
            }
            var red = new RedFrameWindows(rects);
            frame = red;
            FrameCount = red.Count;
        }
        bar.Show();
        if (sharer.HasEnded) CloseAll();
    }
}

/// <summary>主屏顶部的小条。</summary>
public sealed class RemoteSharingBar : Window
{
    private readonly RemoteSharer _sharer;
    private readonly Button _toggle;
    private readonly TextBlock _hotkeyHint;

    public RemoteSharingBar(RemoteSharer sharer)
    {
        _sharer = sharer;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0xB3, 0x16, 0x16));
        _toggle = new Button { Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Foreground = Brushes.White };
        _toggle.Click += async (_, _) => await _sharer.SetControlAsync(!_sharer.ControlAllowed);
        var stop = new Button { Content = L.T("remote.sharing.stop"), Padding = new Thickness(14, 4), Background = Brushes.White, Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0x16, 0x16)), FontWeight = FontWeight.SemiBold };
        stop.Click += async (_, _) => await _sharer.StopAsync(RemoteStopReason.StoppedByUser);
        _hotkeyHint = new TextBlock { Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        var who = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Text = L.F(sharer.ControlAllowed ? "remote.sharing.controlled_by" : "remote.sharing.viewed_by", sharer.PeerName),
        };
        Content = new Border
        {
            Padding = new Thickness(14, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { who, new TextBlock { Text = sharer.PeerFingerprint, Foreground = Brushes.White, Opacity = 0.8, FontFamily = new FontFamily("Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center }, _toggle, stop, _hotkeyHint },
            },
        };
        sharer.ControlChanged += allowed => Dispatcher.UIThread.Post(() =>
        {
            who.Text = L.F(allowed ? "remote.sharing.controlled_by" : "remote.sharing.viewed_by", sharer.PeerName);
            UpdateToggle();
        });
        UpdateToggle();
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } h and not 0) WindowStyles.MakeToolWindow(h);
            PlaceAtTop();
        };
        SizeChanged += (_, _) => PlaceAtTop();
    }

    /// <summary>放在主屏顶部正中（红框里面）。</summary>
    private void PlaceAtTop()
    {
        if (Screens.Primary is not { } primary) return;
        var size = PixelSize.FromSize(Bounds.Size, primary.Scaling);
        var area = primary.WorkingArea;
        Position = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + (int)Math.Ceiling(RemoteSharingOverlay.FrameThickness * primary.Scaling));
    }

    public bool HotkeyAvailable
    {
        set => _hotkeyHint.Text = value ? L.T("remote.sharing.hotkey") : "";
    }

    private void UpdateToggle()
    {
        _toggle.Content = L.T(_sharer.ControlAllowed ? "remote.sharing.revoke" : "remote.sharing.grant");
        // 只看的会话（没有注入）不能改成允许控制。
        _toggle.IsVisible = _sharer.ControlAllowed || _sharer.CanGrantControl;
    }
}
