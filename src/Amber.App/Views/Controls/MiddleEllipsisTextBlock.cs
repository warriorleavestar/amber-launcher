using Amber.Core.Layout;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Amber.App.Views.Controls;

/// <summary>按可用宽度在文件名中间省略，提示和读屏始终保留完整文字。</summary>
public sealed class MiddleEllipsisTextBlock : TextBlock
{
    public static readonly StyledProperty<string> FullTextProperty =
        AvaloniaProperty.Register<MiddleEllipsisTextBlock, string>(
            nameof(FullText), "", coerce: (_, value) => value ?? "");

    public MiddleEllipsisTextBlock()
    {
        // 默认提示以样式优先级跟随原文；后来应用的样式、本地值、绑定和显式 null 均可覆盖。
        // 清除覆盖后绑定会自动恢复，不依赖下一次布局。
        this.Bind(ToolTip.TipProperty, this.GetObservable(FullTextProperty), BindingPriority.Style);
    }

    /// <summary>绑定原文；继承的 Text 同样保留原文，实际省略由文本布局完成。</summary>
    public string FullText
    {
        get => GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    // 继续使用应用里 TextBlock 的字体、前景色和样式类。
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FullTextProperty)
        {
            SetCurrentValue(TextProperty, FullText);
            SetCurrentValue(AutomationProperties.NameProperty, FullText);
            InvalidateTextLayout();
        }
    }

    protected override TextLayout CreateTextLayout(string? text)
    {
        // 基类在测量和排列时都会更新 _constraint，已扣除按当前缩放取整的 Padding。
        // 不在布局过程中写 Text，否则无约束测量后再窄宽排列会不断使测量失效。
        var width = double.IsNaN(_constraint.Width) ? double.PositiveInfinity : _constraint.Width;
        var fitted = MiddleEllipsis.Fit(FullText, width, MeasureText);
        return CreateLayout(fitted, width, TextAlignment);
    }

    private double MeasureText(string text)
    {
        using var layout = CreateLayout(text, double.PositiveInfinity, TextAlignment.Left);
        return layout.WidthIncludingTrailingWhitespace;
    }

    private TextLayout CreateLayout(string text, double width, TextAlignment alignment) =>
        new(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            textAlignment: alignment,
            textWrapping: TextWrapping.NoWrap,
            textTrimming: TextTrimming.None,
            textDecorations: TextDecorations,
            flowDirection: FlowDirection,
            maxWidth: width,
            lineHeight: LineHeight,
            letterSpacing: LetterSpacing,
            maxLines: 1,
            fontFeatures: FontFeatures);

}
