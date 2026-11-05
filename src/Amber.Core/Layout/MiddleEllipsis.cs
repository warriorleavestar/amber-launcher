using System.Globalization;

namespace Amber.Core.Layout;

/// <summary>在文本中间省略内容，尽量保留文件名的开头、结尾和短扩展名。</summary>
public static class MiddleEllipsis
{
    /// <summary>
    /// 按实际测量宽度截短文本。文本元素（包括组合字符和 emoji）始终完整保留。
    /// 测量次数为 O(log n)；测量函数应使用一致的字体和排版设置。
    /// </summary>
    public static string Fit(string text, double maxWidth, Func<string, double> measure, string ellipsis = "…")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(ellipsis);

        if (text.Length == 0 || double.IsPositiveInfinity(maxWidth))
            return text;
        if (!(maxWidth > 0))
            return "";

        var fullWidth = measure(text);
        if (Fits(fullWidth))
            return text;

        var ellipsisWidth = measure(ellipsis);
        if (!Fits(ellipsisWidth))
            return "";

        var starts = StringInfo.ParseCombiningCharacters(text);
        var count = starts.Length;
        if (count == 1)
            return ellipsis;

        var extensionCount = ShortExtensionCount();
        // 先按平均宽度估计，再检查相邻候选。等宽文本通常只需四次测量。
        var estimate = double.IsFinite(fullWidth) && fullWidth > 0
            ? (maxWidth - ellipsisWidth) / fullWidth * count
            : count / 2.0;
        var retained = (int)Math.Clamp(Math.Floor(estimate), 1, count - 1);
        var candidate = Create(retained);
        var low = 0;
        var high = count;
        var best = ellipsis;

        if (Fits(measure(candidate)))
        {
            low = retained;
            best = candidate;
            if (low == count - 1)
                return best;
            candidate = Create(low + 1);
            if (!Fits(measure(candidate)))
                return best;
            low++;
            best = candidate;
        }
        else
        {
            high = retained;
            if (high == 1)
                return best;
            candidate = Create(high - 1);
            if (Fits(measure(candidate)))
                return candidate;
            high--;
        }

        // low 是已测量可容纳的候选，high 是不可容纳的候选（count 为原文哨兵）。
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            candidate = Create(middle);
            if (Fits(measure(candidate)))
            {
                low = middle;
                best = candidate;
            }
            else
            {
                high = middle;
            }
        }

        return best;

        bool Fits(double width) => width >= 0 && width <= maxWidth;

        string Create(int keep)
        {
            var prefixCount = (int)Math.Round(keep * 0.45, MidpointRounding.AwayFromZero);
            // 空间紧张时先把完整短扩展名所需的位置让给后缀。
            prefixCount = Math.Min(prefixCount, Math.Max(0, keep - extensionCount));
            var suffixCount = keep - prefixCount;
            var prefixEnd = starts[prefixCount];
            var suffixStart = suffixCount == 0 ? text.Length : starts[count - suffixCount];
            return string.Concat(text.AsSpan(0, prefixEnd), ellipsis, text.AsSpan(suffixStart));
        }

        int ShortExtensionCount()
        {
            var dot = text.LastIndexOf('.');
            if (dot < 0)
                return 0;
            var element = Array.BinarySearch(starts, dot);
            if (element < 0 || element + 1 == count || starts[element + 1] != dot + 1)
                return 0;
            var length = count - element - 1;
            return length is >= 1 and <= 5 ? length + 1 : 0;
        }
    }
}
