using System.Text.RegularExpressions;

namespace Amber.Core.Launch;

public enum GameLogLevel { Trace, Debug, Info, Warn, Error, Fatal }

public sealed record GameLogLine(DateTimeOffset Time, GameLogLevel Level, string? Thread, string Message);

/// <summary>
/// 把游戏输出转换成普通日志行。新版本的 log4j 配置会输出 XML 事件，
/// 一条事件跨多行；其他输出（Java 报错、旧版本）按原样当作 Info。
/// 非线程安全：一个游戏进程用一个实例，按顺序喂入行。
/// </summary>
public sealed partial class GameLogParser
{
    private GameLogLevel _level = GameLogLevel.Info;
    private string? _thread;
    private DateTimeOffset _time;
    private bool _inEvent;

    /// <summary>喂入一行输出；能产出完整日志行时返回它，否则返回 null。</summary>
    public GameLogLine? Feed(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            _inEvent = true;
            _level = EventLevel().Match(trimmed) is { Success: true } l && Enum.TryParse<GameLogLevel>(l.Groups[1].Value, true, out var parsed) ? parsed : GameLogLevel.Info;
            _thread = EventThread().Match(trimmed) is { Success: true } t ? t.Groups[1].Value : null;
            _time = EventTime().Match(trimmed) is { Success: true } ts && long.TryParse(ts.Groups[1].Value, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                : DateTimeOffset.Now;
            return null;
        }
        if (_inEvent)
        {
            if (trimmed.StartsWith("</log4j:Event", StringComparison.Ordinal)) { _inEvent = false; return null; }
            if (Cdata().Match(trimmed) is { Success: true } m)
                return new GameLogLine(_time, _level, _thread, m.Groups[1].Value);
            return null;
        }
        if (string.IsNullOrWhiteSpace(line)) return null;
        var level = line.Contains("Exception", StringComparison.Ordinal) || line.StartsWith("\tat ", StringComparison.Ordinal)
            ? GameLogLevel.Error
            : GameLogLevel.Info;
        return new GameLogLine(DateTimeOffset.Now, level, null, line);
    }

    [GeneratedRegex("level=\"(\\w+)\"")]
    private static partial Regex EventLevel();

    [GeneratedRegex("thread=\"([^\"]*)\"")]
    private static partial Regex EventThread();

    [GeneratedRegex("timestamp=\"(\\d+)\"")]
    private static partial Regex EventTime();

    [GeneratedRegex(@"<!\[CDATA\[(.*?)(\]\]>|$)", RegexOptions.Singleline)]
    private static partial Regex Cdata();
}
