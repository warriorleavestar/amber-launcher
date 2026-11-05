using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>界面数字的统一写法、状态消息、伪本地化（不切换全局语言，按当前的简体中文区域）。</summary>
public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_550, "1.0 MB")]          // 1023.97 KB 四舍五入成 1024.0 → 进一级
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void SizeUsesOneDecimalAndRoundsIntoNextUnit(long bytes, string expected) => Assert.Equal(expected, Fmt.Size(bytes));

    [Fact]
    public void DurationIsClockUnderAnHour()
    {
        Assert.Equal("0:05", Fmt.Duration(TimeSpan.FromSeconds(5)));
        Assert.Equal("12:34", Fmt.Duration(TimeSpan.FromSeconds(12 * 60 + 34)));
        Assert.Equal("59:59", Fmt.Duration(TimeSpan.FromSeconds(3599)));
        Assert.Equal(L.F("format.duration.hours_minutes", 1, 1), Fmt.Duration(TimeSpan.FromMinutes(61)));
        Assert.Equal(L.P("format.duration.minutes", 1, 1), Fmt.Minutes(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StatusMessageFormatsWhenShown()
    {
        var m = StatusMessage.Warning("home.status.crashed", 1);
        Assert.Equal(StatusKind.Warning, m.Kind);
        Assert.Equal(L.F("home.status.crashed", 1), m.Text);
        Assert.Equal("原样文字", StatusMessage.Plain("原样文字").Text);
        var failure = StatusMessage.Failure("home.status.launch_failed", new IOException("磁盘满了"), "x");
        Assert.Equal(StatusKind.Error, failure.Kind);
        Assert.Contains("磁盘满了", failure.Detail);
    }

    [Fact]
    public void PseudoLocalizationLengthensAndKeepsPlaceholders()
    {
        var text = "已添加 {0} 个数据包";
        var pseudo = L.PseudoLocalize(text);
        Assert.Contains("{0}", pseudo);
        Assert.True(pseudo.Length >= text.Length * 1.4);
        Assert.Equal(string.Format("已添加 {0} 个数据包", 3), string.Format(text, 3));
        Assert.Equal("", L.PseudoLocalize(""));
    }
}
