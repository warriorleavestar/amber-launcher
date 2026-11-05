using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

/// <summary>
/// 两步确认的删除按钮（改版规格 3.9）：第一次点变成实心红底的“确认删除”，4 秒内再点一次才执行 Command；
/// 待确认状态在指针移开后一直保持，按 Esc 或超时取消。两种文字的宽度取大的那个，切换时按钮不变宽变窄。
/// 对象名放在 ToolTip 和读屏名字里，按钮上只写动作。
/// </summary>
public class ConfirmButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public static readonly StyledProperty<string?> ConfirmTextProperty =
        AvaloniaProperty.Register<ConfirmButton, string?>(nameof(ConfirmText));

    public static readonly StyledProperty<string?> ObjectNameProperty =
        AvaloniaProperty.Register<ConfirmButton, string?>(nameof(ObjectName));

    public static readonly TimeSpan ArmDuration = TimeSpan.FromSeconds(4);

    /// <summary>全局取消（窗口里按 Esc 时调用）：所有待确认的按钮都退回去。</summary>
    public static event Action? CancelAllRequested;

    public static void CancelAll() => CancelAllRequested?.Invoke();

    private readonly DispatcherTimer _timer = new() { Interval = ArmDuration };
    private object? _normalContent;

    /// <summary>待确认时显示的文字，默认“确认删除”。</summary>
    public string? ConfirmText
    {
        get => GetValue(ConfirmTextProperty);
        set => SetValue(ConfirmTextProperty, value);
    }

    /// <summary>要删的东西叫什么（ToolTip、读屏用）。</summary>
    public string? ObjectName
    {
        get => GetValue(ObjectNameProperty);
        set => SetValue(ObjectNameProperty, value);
    }

    public bool IsArmed { get; private set; }

    public ConfirmButton()
    {
        Classes.Add("danger");
        _timer.Tick += (_, _) => Disarm();
    }

    private string Confirm => ConfirmText ?? L.T("common.confirm_delete");

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CancelAllRequested += Disarm;
        L.Changed += OnLanguageChanged;
        UpdateNames();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelAllRequested -= Disarm;
        L.Changed -= OnLanguageChanged;
        Disarm();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (IsArmed) Content = Confirm;
        InvalidateMeasure();
        UpdateNames();
    });

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ObjectNameProperty || change.Property == ContentProperty) UpdateNames();
    }

    protected override void OnClick()
    {
        if (!IsArmed)
        {
            Arm();
            return;
        }
        Disarm();
        base.OnClick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsArmed)
        {
            Disarm();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>宽度按两种文字里宽的那个算，避免切换时布局跳动。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var normal = (IsArmed ? _normalContent : Content) as string;
        var widest = Math.Max(TextWidth(normal), TextWidth(Confirm));
        var chrome = Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
        return new Size(Math.Max(size.Width, Math.Min(availableSize.Width, widest + chrome)), size.Height);
    }

    private double TextWidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground);
        return Math.Ceiling(layout.WidthIncludingTrailingWhitespace);
    }

    private void Arm()
    {
        IsArmed = true;
        _normalContent = Content;
        Content = Confirm;
        Classes.Set("armed", true);
        _timer.Stop();
        _timer.Start();
        UpdateNames();
    }

    private void Disarm()
    {
        _timer.Stop();
        if (!IsArmed) return;
        IsArmed = false;
        Content = _normalContent;
        Classes.Set("armed", false);
        UpdateNames();
    }

    private void UpdateNames()
    {
        var action = IsArmed ? Confirm : Content as string ?? "";
        var name = string.IsNullOrEmpty(ObjectName) ? action : action + " " + ObjectName;
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, string.IsNullOrEmpty(ObjectName) ? null : name);
    }
}
