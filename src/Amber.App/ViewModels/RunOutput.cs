using Amber.Core.Launch;

namespace Amber.App.ViewModels;

/// <summary>
/// 上一局游戏的最后几百行输出（崩溃分析和错误报告用）。
/// 是不是无痕模式按这一局真正交给 launcher.Launch 的参数记下，启动后再改设置也不影响这一局的错误报告。
/// </summary>
internal sealed class RunOutput
{
    public const int Kept = 400;

    private readonly object _gate = new();
    private readonly Queue<ChatLogFilter.OutputLine> _lines = new();
    private ChatLogFilter.OutputFilter _filter = new();

    /// <summary>这一局开着无痕模式：导出错误报告时去掉聊天行。</summary>
    public bool NoTrace { get; private set; }

    public void Begin(LaunchOptions options)
    {
        lock (_gate)
        {
            _lines.Clear();
            _filter = new ChatLogFilter.OutputFilter();
            NoTrace = options.NoTrace;
        }
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            // 先识别事件再裁剪：聊天首行被挤出队列后，余下的续行仍带着聊天标记。
            _lines.Enqueue(_filter.Add(line));
            if (_lines.Count > Kept) _lines.Dequeue();
        }
    }

    public string[] Lines { get { lock (_gate) return _lines.Select(l => l.Text).ToArray(); } }

    /// <summary>写进错误报告的输出：无痕模式下去掉聊天（Amber 自己的“[Amber] ”行不动）。</summary>
    public List<string> ForExport()
    {
        lock (_gate) return _lines.Where(l => !NoTrace || !l.IsChat).Select(l => l.Text).ToList();
    }
}
