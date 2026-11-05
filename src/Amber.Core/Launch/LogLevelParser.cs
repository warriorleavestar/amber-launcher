namespace Amber.Core.Launch;

/// <summary>识别 Minecraft 文本日志的等级；没有等级标记时保留调用方提供的等级。</summary>
public static class LogLevelParser
{
    public static GameLogLevel Parse(string message, GameLogLevel fallback = GameLogLevel.Info)
    {
        var text = message.AsSpan().TrimStart();
        if (text.IsEmpty) return fallback;

        // 只读行首的日志头，不能把正文里的 ERROR 或模组名当成日志等级。
        if (ReadHeader(ref text, out var header))
        {
            if (TryHeaderLevel(header, out var level)) return level;
            if (IsTime(header) && ReadHeader(ref text, out header) && TryHeaderLevel(header, out level)) return level;
        }

        text = message.AsSpan().TrimStart();
        if (text.StartsWith("Exception in thread ", StringComparison.Ordinal)
            || text.StartsWith("Caused by:", StringComparison.Ordinal)
            || text.StartsWith("Suppressed:", StringComparison.Ordinal)
            || (message.Length > text.Length && text.StartsWith("at ", StringComparison.Ordinal)))
            return GameLogLevel.Error;

        return fallback;
    }

    private static bool ReadHeader(ref ReadOnlySpan<char> text, out ReadOnlySpan<char> header)
    {
        text = text.TrimStart();
        var end = text.IndexOf(']');
        if (text.IsEmpty || text[0] != '[' || end < 1)
        {
            header = default;
            return false;
        }
        header = text[1..end].Trim();
        text = text[(end + 1)..];
        return true;
    }

    private static bool TryHeaderLevel(ReadOnlySpan<char> header, out GameLogLevel level)
    {
        if (TryLevel(header, out level)) return true;

        // 新版：[Render thread/WARN]；线程名里可以有空格。
        var slash = header.LastIndexOf('/');
        if (slash > 0 && TryLevel(header[(slash + 1)..], out level)) return true;

        // 旧版服务端：[16:00:00 INFO]。
        var space = header.LastIndexOf(' ');
        return space > 0 && IsTime(header[..space].TrimEnd()) && TryLevel(header[(space + 1)..], out level);
    }

    private static bool TryLevel(ReadOnlySpan<char> text, out GameLogLevel level)
    {
        if (text.Equals("TRACE", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Trace;
        else if (text.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Debug;
        else if (text.Equals("INFO", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Info;
        else if (text.Equals("WARN", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Warn;
        else if (text.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Error;
        else if (text.Equals("FATAL", StringComparison.OrdinalIgnoreCase)) level = GameLogLevel.Fatal;
        else
        {
            level = default;
            return false;
        }
        return true;
    }

    private static bool IsTime(ReadOnlySpan<char> text)
    {
        if (text.Length < 8 || text[2] != ':' || text[5] != ':') return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (i is 2 or 5) continue;
            if (i == 8 && text[i] is '.' or ',') continue;
            if (!char.IsAsciiDigit(text[i])) return false;
        }
        return text.Length == 8 || (text.Length > 9 && text[8] is '.' or ',');
    }
}
