using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

/// <summary>共享状态条：绑定一个 StatusMessage（为 null 时收起）。换语言时重新格式化。</summary>
public partial class StatusBar : UserControl
{
    public static readonly StyledProperty<StatusMessage?> MessageProperty =
        AvaloniaProperty.Register<StatusBar, StatusMessage?>(nameof(Message));

    /// <summary>成功消息显示多久后自动消失。</summary>
    public static readonly TimeSpan SuccessDismissAfter = TimeSpan.FromSeconds(5);

    /// <summary>警告多留一会儿再自动消失，给人看清楚的时间。</summary>
    public static readonly TimeSpan WarningDismissAfter = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _autoDismiss = new();
    private StatusMessage? _dismissed;
    private bool _isShowing;

    /// <summary>复制按钮现在显示什么：自己记着，按语言写字（不覆盖绑定，换语言后也对）。</summary>
    private string _copyKey = "common.copy";
    private int _copyVersion;

    /// <summary>现在有没有显示消息（没消息或者被 ✕ 关掉时为 false）：外面只装着状态条的卡片跟着收起。</summary>
    public static readonly DirectProperty<StatusBar, bool> IsShowingProperty =
        AvaloniaProperty.RegisterDirect<StatusBar, bool>(nameof(IsShowing), o => o.IsShowing);

    public bool IsShowing
    {
        get => _isShowing;
        private set => SetAndRaise(IsShowingProperty, ref _isShowing, value);
    }

    public StatusMessage? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public StatusBar()
    {
        InitializeComponent();
        _autoDismiss.Tick += (_, _) => OnAutoDismissTick();
        // 鼠标停在条上（正在看、正要点详情或 ✕）时不自动关，移开后重新计时。
        Bar.PointerEntered += (_, _) => _autoDismiss.Stop();
        Bar.PointerExited += (_, _) => RestartAutoDismiss();
        CloseButton.Click += (_, _) => Dismiss();
        CopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && Message?.Detail is { } detail)
            {
                var version = ++_copyVersion;
                try
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, detail);
                    SetCopyState("common.copied");
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
                {
                    // 剪贴板被别的程序占着：只在按钮上说一声，不让程序崩掉。
                    SetCopyState("common.copy_failed");
                }
                await Task.Delay(1500);
                // 这期间又点了一次：让后一次自己恢复。
                if (version == _copyVersion) SetCopyState("common.copy");
            }
        };
        SetCopyState("common.copy");
        AutomationProperties.SetName(CloseButton, L.T("common.close"));
        ToolTip.SetTip(CloseButton, L.T("common.close"));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        L.Changed += OnLanguageChanged;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        L.Changed -= OnLanguageChanged;
        _autoDismiss.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageProperty)
        {
            _dismissed = null;
            Render();
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        AutomationProperties.SetName(CloseButton, L.T("common.close"));
        ToolTip.SetTip(CloseButton, L.T("common.close"));
        SetCopyState(_copyKey);
        Render(restartTimer: false);
    });

    private void SetCopyState(string key)
    {
        _copyKey = key;
        CopyButton.Content = L.T(key);
    }

    /// <summary>关掉当前这条（同一条消息不会再显示，新消息来了照常显示）。</summary>
    private void Dismiss()
    {
        _dismissed = Message;
        Render();
    }

    private void OnAutoDismissTick()
    {
        // 这一条不该自动关（计时已停）；详情还开着或鼠标还在条上：先不关，等下一轮。
        if (!_autoDismiss.IsEnabled || DetailButton.Flyout is { IsOpen: true } || Bar.IsPointerOver) return;
        Dismiss();
    }

    /// <summary>这一级的消息多久后自动消失；null 是不自动消失（错误要人看到；信息多是“正在……”这类进行中的状态）。</summary>
    internal static TimeSpan? AutoDismissDelay(StatusKind kind) => kind switch
    {
        StatusKind.Success => SuccessDismissAfter,
        StatusKind.Warning => WarningDismissAfter,
        _ => null,
    };

    private void RestartAutoDismiss()
    {
        _autoDismiss.Stop();
        if (!IsShowing || Message is not { } m || AutoDismissDelay(m.Kind) is not { } delay) return;
        _autoDismiss.Interval = delay;
        _autoDismiss.Start();
    }

    /// <summary>自动消失的计时是不是在走（测试用）。</summary>
    internal bool AutoDismissPending => _autoDismiss.IsEnabled;

    /// <summary>当作计时到了（测试用，不用真等几秒）。</summary>
    internal void ElapseAutoDismissForTest() => OnAutoDismissTick();

    /// <summary>复制按钮现在的状态键（测试用）。</summary>
    internal string CopyStateKey => _copyKey;

    private void Render(bool restartTimer = true)
    {
        var m = Message;
        var visible = m is not null && !ReferenceEquals(m, _dismissed) && m.Key.Length > 0;
        Bar.IsVisible = visible;
        IsShowing = visible;
        if (!visible)
        {
            _autoDismiss.Stop();
            return;
        }
        var text = m!.Format();
        MessageText.Text = text;
        ToolTip.SetTip(MessageText, text);
        AutomationProperties.SetName(MessageText, text);
        Icon.Text = m.Kind switch
        {
            StatusKind.Success => "✓",
            StatusKind.Warning => "⚠",
            StatusKind.Error => "✕",
            _ => "",
        };
        Icon.IsVisible = Icon.Text.Length > 0;
        Bar.Classes.Set("success", m.Kind == StatusKind.Success);
        Bar.Classes.Set("warning", m.Kind == StatusKind.Warning);
        Bar.Classes.Set("error", m.Kind == StatusKind.Error);
        DetailButton.IsVisible = !string.IsNullOrEmpty(m.Detail);
        DetailText.Text = m.Detail;
        if (restartTimer) RestartAutoDismiss();
    }
}
