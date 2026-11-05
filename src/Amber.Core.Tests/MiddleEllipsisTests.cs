using System.Globalization;
using System.Runtime.ExceptionServices;
using Amber.App.Views.Controls;
using Amber.Core.Layout;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Amber.Core.Tests;

[Collection("MiddleEllipsis control")]
public class MiddleEllipsisTests
{
    [Theory]
    [InlineData("", 20, "")]
    [InlineData("short.jar", 20, "short.jar")]
    [InlineData("short.jar", 9, "short.jar")]
    [InlineData("long-name.jar", 0, "")]
    [InlineData("long-name.jar", -1, "")]
    [InlineData("long-name.jar", 0.5, "")]
    [InlineData("long-name.jar", 1, "…")]
    [InlineData("x", 0.5, "")]
    public void HandlesEmptyFullAndNarrowWidths(string text, double width, string expected) =>
        Assert.Equal(expected, MiddleEllipsis.Fit(text, width, s => s.Length));

    [Fact]
    public void InvalidAndUnlimitedWidthsDoNotNeedMeasurement()
    {
        double Unexpected(string _) => throw new InvalidOperationException("不应测量");
        Assert.Equal("", MiddleEllipsis.Fit("text", double.NaN, Unexpected));
        Assert.Equal("", MiddleEllipsis.Fit("text", double.NegativeInfinity, Unexpected));
        Assert.Equal("text", MiddleEllipsis.Fit("text", double.PositiveInfinity, Unexpected));
        Assert.Equal("", MiddleEllipsis.Fit("", 20, Unexpected));
    }

    [Fact]
    public void KeepsRoughlyFortyFivePercentAtTheFrontAndFiftyFiveAtTheEnd()
    {
        Assert.Equal("ABCDEFGHI…PQRSTUVWXYZ", MiddleEllipsis.Fit("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 21, s => s.Length));
    }

    [Theory]
    [InlineData("sodium-fabric-0.6.0+mc1.21.1.jar", 9, "sodi….jar")]
    [InlineData("very-long-name.json", 6, "….json")]
    [InlineData("very-long-name.a", 3, "….a")]
    [InlineData("very-long-name.abcde", 7, "….abcde")]
    public void GivesShortExtensionsPriority(string text, int width, string expected) =>
        Assert.Equal(expected, MiddleEllipsis.Fit(text, width, s => s.Length));

    [Theory]
    [InlineData("very-long-name.abcdef")]
    [InlineData("very-long-name.")]
    [InlineData("very-long-name")]
    public void LongOrMissingExtensionsUseTheUsualSplit(string text)
    {
        var result = MiddleEllipsis.Fit(text, 7, s => s.Length);
        Assert.Equal(text[..3] + "…" + text[^3..], result);
    }

    [Fact]
    public void KeepsChineseTextOnBothSides()
    {
        Assert.Equal("甲乙丙…壬癸子丑", MiddleEllipsis.Fit("甲乙丙丁戊己庚辛壬癸子丑", 8, Elements));
    }

    [Fact]
    public void DoesNotSplitSurrogatesCombiningMarksOrEmojiSequences()
    {
        const string text = "甲👩‍💻乙e\u0301丙👍🏽丁👨‍👩‍👧‍👦戊🇳🇿己";
        var result = MiddleEllipsis.Fit(text, 8, Elements);
        Assert.Equal("甲👩‍💻乙…👨‍👩‍👧‍👦戊🇳🇿己", result);
        Assert.Equal(8, Elements(result));
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👍🏽")]
    public void RepeatedTextElementsStayWhole(string element)
    {
        var text = string.Concat(Enumerable.Repeat(element, 40));
        var result = MiddleEllipsis.Fit(text, 10, Elements);
        Assert.Equal(string.Concat(Enumerable.Repeat(element, 4)) + "…" + string.Concat(Enumerable.Repeat(element, 5)), result);
    }

    [Fact]
    public void UsesMeasuredWidthInsteadOfCharacterCount()
    {
        static double Measure(string text) => text.Sum(c => c == 'W' ? 5.0 : 1.0);
        var result = MiddleEllipsis.Fit("WiiiiiiiiiiiiiiiW", 11, Measure);
        Assert.Equal("W…W", result);
        Assert.True(Measure(result) <= 11);
    }

    [Fact]
    public void SupportsCustomAndEmptyEllipses()
    {
        Assert.Equal("ab...hij", MiddleEllipsis.Fit("abcdefghij", 8, s => s.Length, "..."));
        Assert.Equal("", MiddleEllipsis.Fit("abcdefghij", 2, s => s.Length, "..."));
        Assert.Equal("abhij", MiddleEllipsis.Fit("abcdefghij", 5, s => s.Length, ""));
    }

    [Fact]
    public void TwoHundredCharactersFitIntoTwentyWithAtMostSevenMeasurements()
    {
        var calls = 0;
        var result = MiddleEllipsis.Fit(new string('x', 200), 20, s => { calls++; return s.Length; });
        Assert.Equal(new string('x', 9) + "…" + new string('x', 10), result);
        Assert.InRange(calls, 1, 7);
    }

    [Fact]
    public void UnevenWidthsStillNeedOnlyLogarithmicallyManyMeasurements()
    {
        var text = new string('i', 2_000) + new string('W', 2_000);
        var calls = 0;
        double Measure(string value)
        {
            calls++;
            return value.Sum(c => c == 'W' ? 10.0 : 1.0);
        }
        var result = MiddleEllipsis.Fit(text, 70, Measure);
        Assert.True(result.Sum(c => c == 'W' ? 10.0 : 1.0) <= 70);
        Assert.InRange(calls, 1, 4 + (int)Math.Ceiling(Math.Log2(4_000)));
    }

    [Fact]
    public void RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => MiddleEllipsis.Fit(null!, 20, s => s.Length));
        Assert.Throws<ArgumentNullException>(() => MiddleEllipsis.Fit("text", 20, null!));
        Assert.Throws<ArgumentNullException>(() => MiddleEllipsis.Fit("text", 20, s => s.Length, null!));
    }

    /// <summary>只初始化渲染平台，离屏检查真实控件，不启动启动器或读写游戏目录。</summary>
    [Fact]
    public void ControlSelfCheckUsesRealTextLayoutWithoutInvalidationLoops()
    {
        if (!OperatingSystem.IsWindows()) return;
        UiTestThread.Run(CheckControl);
    }

    private static void CheckControl()
    {
        const string filename = "sodium-fabric-0.6.0+mc1.21.1.jar";
        var block = new MiddleEllipsisTextBlock
        {
            FullText = filename,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            Padding = new Thickness(8),
            UseLayoutRounding = false,
        };

        void Layout(double width)
        {
            block.Measure(new Size(width, 80));
            block.Arrange(new Rect(0, 0, width, 80));
            Assert.True(block.IsMeasureValid);
            Assert.True(block.IsArrangeValid);
            Assert.True(block.TextLayout.WidthIncludingTrailingWhitespace <= Math.Max(0, width - 16));
        }

        string Displayed() => string.Concat(block.TextLayout.TextLines
            .SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));

        // 无约束测量再给窄宽度排列，是横向容器里最容易出现重布局循环的情况。
        block.Measure(Size.Infinity);
        block.Arrange(new Rect(0, 0, 160, 80));
        Assert.True(block.IsMeasureValid);
        Assert.True(block.IsArrangeValid);
        var narrow = Displayed();
        Assert.Contains("…", narrow);
        Assert.EndsWith(".jar", narrow);
        Assert.True(block.TextLayout.WidthIncludingTrailingWhitespace <= 144);
        block.Measure(Size.Infinity);
        Assert.Equal(narrow, Displayed());
        Assert.Equal(filename, block.Text);
        Assert.Equal(filename, ToolTip.GetTip(block));
        Assert.Equal(filename, AutomationProperties.GetName(block));
        Assert.Equal(filename, ControlAutomationPeer.CreatePeerForElement(block)!.GetName());

        Layout(700);
        Assert.Equal(filename, Displayed());
        Layout(160);
        block.FontSize = 24;
        Layout(160);
        Assert.True(Elements(Displayed()) < Elements(narrow));
        block.LetterSpacing = 2;
        block.FontWeight = FontWeight.Bold;
        Layout(160);
        Assert.EndsWith(".jar", Displayed());

        // 在显示宽度变化之后走一次真实的位图渲染。
        using (var bitmap = new RenderTargetBitmap(new PixelSize(160, 80)))
            bitmap.Render(block);

        block.FullText = "模组😀e\u0301-文件.jar";
        Layout(700);
        Assert.Equal(block.FullText, Displayed());
        Assert.Equal(block.FullText, ToolTip.GetTip(block));
        Assert.Equal(block.FullText, ControlAutomationPeer.CreatePeerForElement(block)!.GetName());
        Layout(16);
        // Avalonia 的空行布局可能含有一个零宽占位空格。
        Assert.True(string.IsNullOrWhiteSpace(Displayed()));

        ToolTip.SetTip(block, "custom");
        block.FullText = filename;
        Layout(160);
        Assert.Equal("custom", ToolTip.GetTip(block));
        ToolTip.SetTip(block, null);
        block.FullText = "next.jar";
        Layout(160);
        Assert.Null(ToolTip.GetTip(block));
        block.ClearValue(ToolTip.TipProperty);
        Layout(160);
        Assert.Equal(block.FullText, ToolTip.GetTip(block));

        // 外部样式和绑定也应优先于自动提示。
        var styled = new MiddleEllipsisTextBlock { FullText = filename };
        var host = new Border { Child = styled };
        host.Styles.Add(new Style(s => s.OfType<TextBlock>())
        {
            Setters = { new Setter(ToolTip.TipProperty, "styled") },
        });
        host.Measure(new Size(160, 80));
        host.Arrange(new Rect(0, 0, 160, 80));
        styled.FullText = "changed.jar";
        Assert.Equal("styled", ToolTip.GetTip(styled));
        var tipSource = new TextBlock { Text = "bound" };
        using var binding = styled.Bind(ToolTip.TipProperty, tipSource.GetObservable(TextBlock.TextProperty));
        styled.FullText = filename;
        Assert.Equal("bound", ToolTip.GetTip(styled));
        tipSource.Text = "updated";
        Assert.Equal("updated", ToolTip.GetTip(styled));
    }

    private static double Elements(string text) => StringInfo.ParseCombiningCharacters(text).Length;
}

// 初始化桌面渲染平台会改变进程级状态，不能与其他测试并行。
[CollectionDefinition("MiddleEllipsis control", DisableParallelization = true)]
public sealed class MiddleEllipsisControlCollection;
