using Amber.Core.Launch;

namespace Amber.App.ViewModels;

/// <summary>
/// 上一局游戏的最后几百行输出（崩溃分析和错误报告用）。
/// 是不是无痕模式按这一局真正交给 launcher.Launch 的参数记下，启动后再改设置也不影响这一局的错误报告。
/// </summary>
internal sealed class RunOutput
{
    public const int Kept = 400;

    private readonly Queue<string> _lines = new();

    /// <summary>这一局开着无痕模式：导出错误报告时去掉聊天行。</summary>
    public bool NoTrace { get; private set; }

    public void Begin(LaunchOptions options)
    {
        _lines.Clear();
        NoTrace = options.NoTrace;
    }

    public void Add(string line)
    {
        _lines.Enqueue(line);
        if (_lines.Count > Kept) _lines.Dequeue();
    }

    public string[] Lines => _lines.ToArray();

    /// <summary>写进错误报告的输出：无痕模式下去掉聊天（Amber 自己的“[Amber] ”行不动）。</summary>
    public List<string> ForExport() => NoTrace ? ChatLogFilter.WithoutChat(_lines).ToList() : [.. _lines];
}
