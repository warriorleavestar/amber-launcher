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

    /// <summary>成功消息多久之后淡成中性色。</summary>
    public static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _settle = new() { Interval = SettleAfter };
    private StatusMessage? _dismissed;
    private bool _isShowing;

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
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Bar.Classes.Set("settled", true);
        };
        CloseButton.Click += (_, _) =>
        {
            _dismissed = Message;
            Render();
        };
        CopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && Message?.Detail is { } detail)
            {
                try
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, detail);
                    CopyButton.Content = L.T("common.copied");
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
                {
                    // 剪贴板被别的程序占着：只在按钮上说一声，不让程序崩掉。
                    CopyButton.Content = L.T("common.copy_failed");
                }
                await Task.Delay(1500);
                CopyButton.Content = L.T("common.copy");
            }
        };
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
        _settle.Stop();
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
        Render(restartTimer: false);
    });

    private void Render(bool restartTimer = true)
    {
        var m = Message;
        var visible = m is not null && !ReferenceEquals(m, _dismissed) && m.Key.Length > 0;
        Bar.IsVisible = visible;
        IsShowing = visible;
        if (!visible)
        {
            _settle.Stop();
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
        // 只有错误要手动关；警告、信息、成功下一条消息来了就换掉。
        CloseButton.IsVisible = m.Kind == StatusKind.Error;
        if (restartTimer)
        {
            Bar.Classes.Set("settled", false);
            _settle.Stop();
            if (m.Kind == StatusKind.Success) _settle.Start();
        }
    }
}
