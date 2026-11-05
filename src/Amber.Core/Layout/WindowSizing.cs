namespace Amber.Core.Layout;

/// <summary>
/// 启动器窗口的默认大小：按屏幕可用区域的比例算，再夹在最小和最大之间，不对着某一种屏幕定死。
/// 所有数都是逻辑像素（DIP），和缩放无关。
/// </summary>
public static class WindowSizing
{
    /// <summary>最小窗口：放得下收起的侧栏和一列内容。屏幕比这还小时就用整个可用区域。</summary>
    public const double MinWidth = 760;
    public const double MinHeight = 440;

    /// <summary>默认占可用区域的比例：留出桌面，又不至于在小屏幕上太挤。</summary>
    public const double WidthFraction = 0.75;
    public const double HeightFraction = 0.8;

    /// <summary>默认大小的上限：大屏幕、低缩放（比如 4K 开 100%）下不一打开就铺满，用户要大可以自己拉或最大化。</summary>
    public const double DefaultMaxWidth = 1600;
    public const double DefaultMaxHeight = 1000;

    /// <summary>
    /// 第一次打开时的客户区大小。workWidth/workHeight 是可用区域（扣掉任务栏），chromeHeight 是标题栏高度
    /// （不知道时传 0，窗口打开后再按实际外框收一次）。
    /// </summary>
    public static (double Width, double Height) Default(double workWidth, double workHeight, double chromeHeight = 0)
    {
        var (maxW, maxH) = MaxClient(workWidth, workHeight, chromeHeight);
        var w = Math.Min(Math.Clamp(workWidth * WidthFraction, MinWidth, DefaultMaxWidth), maxW);
        var h = Math.Min(Math.Clamp((workHeight - chromeHeight) * HeightFraction, MinHeight, DefaultMaxHeight), maxH);
        return (w, h);
    }

    /// <summary>上次记下的大小：还按原样恢复，只是不超过现在的可用区域，也不小于最小（屏幕更小时除外）。</summary>
    public static (double Width, double Height) Restore(double savedWidth, double savedHeight, double workWidth, double workHeight, double chromeHeight = 0)
    {
        var (maxW, maxH) = MaxClient(workWidth, workHeight, chromeHeight);
        var (minW, minH) = (Math.Min(MinWidth, maxW), Math.Min(MinHeight, maxH));
        return (Math.Clamp(Valid(savedWidth, maxW), minW, maxW), Math.Clamp(Valid(savedHeight, maxH), minH, maxH));
    }

    /// <summary>窗口允许的最小客户区：一般是 MinWidth×MinHeight，屏幕放不下时缩到可用区域。</summary>
    public static (double Width, double Height) Minimum(double workWidth, double workHeight, double chromeHeight = 0)
    {
        var (maxW, maxH) = MaxClient(workWidth, workHeight, chromeHeight);
        return (Math.Min(MinWidth, maxW), Math.Min(MinHeight, maxH));
    }

    private static (double W, double H) MaxClient(double workWidth, double workHeight, double chromeHeight) =>
        (Math.Max(1, workWidth), Math.Max(1, workHeight - Math.Max(0, chromeHeight)));

    private static double Valid(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}
