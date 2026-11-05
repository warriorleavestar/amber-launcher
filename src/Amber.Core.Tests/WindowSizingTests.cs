using Amber.Core.Layout;

namespace Amber.Core.Tests;

public class WindowSizingTests
{
    // 可用区域（逻辑像素）：各种屏幕和缩放，不只是某一台电脑。
    [Theory]
    [InlineData(853, 432)]    // 1280×720 开 150%
    [InlineData(1093, 720)]   // 1366×768 开 125%
    [InlineData(1280, 688)]   // 1920×1080 开 150%
    [InlineData(1920, 1032)]  // 1920×1080 开 100%
    [InlineData(1463, 789)]   // 2560×1440 开 175%
    [InlineData(2560, 1392)]  // 2560×1440 开 100%
    [InlineData(3440, 1392)]  // 3440×1440 超宽
    [InlineData(3840, 2112)]  // 4K 开 100%
    public void DefaultFitsInsideWorkAreaAndIsAFractionOfIt(double workW, double workH)
    {
        var (w, h) = WindowSizing.Default(workW, workH, chromeHeight: 30);
        Assert.True(w <= workW && h <= workH - 30, $"{w}×{h} 超出 {workW}×{workH}");
        Assert.True(w >= Math.Min(WindowSizing.MinWidth, workW));
        Assert.True(h >= Math.Min(WindowSizing.MinHeight, workH - 30));
        Assert.True(w <= WindowSizing.DefaultMaxWidth && h <= WindowSizing.DefaultMaxHeight);
    }

    [Fact]
    public void DefaultScalesWithTheScreenInsteadOfOneFixedSize()
    {
        var small = WindowSizing.Default(1280, 688);
        var large = WindowSizing.Default(1920, 1032);
        Assert.True(large.Width > small.Width && large.Height > small.Height);
        Assert.Equal(1280 * WindowSizing.WidthFraction, small.Width, 3);
    }

    [Fact]
    public void ScreensSmallerThanTheMinimumUseTheWholeWorkArea()
    {
        var (w, h) = WindowSizing.Default(700, 400, chromeHeight: 30);
        Assert.Equal(700, w);
        Assert.Equal(370, h);
        Assert.Equal((700.0, 370.0), WindowSizing.Minimum(700, 400, 30));
    }

    [Fact]
    public void RestoreKeepsTheSavedSizeButClampsItToTheCurrentScreen()
    {
        Assert.Equal((1200.0, 800.0), WindowSizing.Restore(1200, 800, 1920, 1032));
        // 换到小屏幕后上次的大小放不下。
        Assert.Equal((853.0, 402.0), WindowSizing.Restore(1200, 800, 853, 432, chromeHeight: 30));
        // 坏数据（0、NaN）当成“尽量大”。
        Assert.Equal((1920.0, 1032.0), WindowSizing.Restore(double.NaN, 0, 1920, 1032));
        // 比最小还小的旧数据拉到最小。
        Assert.Equal((WindowSizing.MinWidth, WindowSizing.MinHeight), WindowSizing.Restore(300, 200, 1920, 1032));
    }
}
