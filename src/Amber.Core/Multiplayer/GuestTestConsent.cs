namespace Amber.Core.Multiplayer;

/// <summary>房主发来的全条件测试请求（加入方界面要用户确认）。</summary>
public sealed record TestRequest(string Run, bool WantsFullLan);

/// <summary>
/// 加入方对全条件测试的同意：只有本机用户点了同意，房主发来的“切换这一步”“提权”才会执行。
/// 完整局域网和把房主当出口要分别勾选。步骤号只能往前走（重复、乱序、旧的都不认），
/// 测试结束、出错、取消或者太久没动静时清掉同意，交回同意时的联机设置用来恢复。
/// </summary>
public sealed class GuestTestConsent
{
    /// <summary>同意后这么久没有收到下一步，就当测试已经结束（房主掉线、崩溃）。</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    /// <summary>一次测试从同意起最长这么久，到了就结束（不管房主还在不在发步骤）。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>一次测试最多执行这么多步（完整的测试计划最多 21 步）。</summary>
    public const int MaxSteps = 24;

    /// <summary>等用户确认的请求放这么久就作废（房主那边也只等这么久）。</summary>
    public static readonly TimeSpan PendingLimit = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private TestRequest? _pending;
    private DateTime _pendingAt;
    private Active? _active;
    private string? _restoringRun;

    private sealed record Active(string Run, bool FullLan, bool Exit, EasyTierOptions? Original, DateTime Started)
    {
        public int LastIndex { get; set; }
        public int Steps { get; set; }
        public DateTime LastActivity { get; set; }
        public bool StepRunning { get; set; }
    }

    public GuestTestConsent(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>等用户确认的请求。</summary>
    public TestRequest? Pending { get { lock (_gate) return PendingLocked(); } }

    private TestRequest? PendingLocked()
    {
        if (_pending is not null && _now() - _pendingAt > PendingLimit) _pending = null;
        return _pending;
    }

    /// <summary>正在进行（已同意）的测试。</summary>
    public string? ActiveRun { get { lock (_gate) return _active?.Run; } }

    /// <summary>已经撤销同意、还在恢复原设置的测试；恢复完之前不能接受下一次测试。</summary>
    public string? RestoringRun { get { lock (_gate) return _restoringRun; } }

    /// <summary>收到房主的请求：已经在测或已有待确认的请求时不认（返回 false）。</summary>
    public bool Request(TestRequest request) => Request(request, out _);

    /// <summary>在同一把锁里返回拒绝时是否正在恢复，避免恢复恰好结束时漏掉给房主的拒绝答复。</summary>
    internal bool Request(TestRequest request, out bool restoring)
    {
        lock (_gate)
        {
            restoring = _restoringRun is not null;
            if (_active is not null || restoring || PendingLocked() is not null || request.Run.Length is < 8 or > 64) return false;
            _pending = request;
            _pendingAt = _now();
            return true;
        }
    }

    /// <summary>用户的回答。同意时记下当前联机设置，测完恢复。返回实际同意的范围；不同意或没有请求时返回 null。</summary>
    public (string Run, bool FullLan, bool Exit)? Answer(bool accept, bool allowFullLan, bool allowExit, EasyTierOptions? original)
    {
        lock (_gate)
        {
            if (PendingLocked() is not { } request) return null;
            _pending = null;
            if (!accept) return null;
            var fullLan = request.WantsFullLan && allowFullLan;
            var exit = fullLan && allowExit;
            _active = new Active(request.Run, fullLan, exit, original, _now()) { LastActivity = _now() };
            return (request.Run, fullLan, exit);
        }
    }

    /// <summary>撤回还没回答的请求（房主取消了）。撤回了返回 true。</summary>
    public bool Withdraw(string run)
    {
        lock (_gate)
        {
            if (_pending?.Run != run) return false;
            _pending = null;
            return true;
        }
    }

    /// <summary>这次请求放太久了：作废它，返回 true（界面收起确认）。</summary>
    public bool ExpirePending(string run)
    {
        lock (_gate)
        {
            var before = _pending;
            return before?.Run == run && PendingLocked() is null;
        }
    }

    /// <summary>房主要提权：要在已同意的测试里、并且勾了完整局域网。</summary>
    public bool AllowsElevate(string? run)
    {
        lock (_gate)
        {
            if (!IsCurrent(run)) return false;
            _active!.LastActivity = _now();
            return _active.FullLan;
        }
    }

    /// <summary>
    /// 房主发来一步：检查是不是这次测试的、步骤号比上一步大、上一步已经做完、模式在同意范围内。
    /// 通过时记下步骤号并返回 null；不通过返回原因（不执行）。
    /// </summary>
    public string? BeginStep(string? run, int index, bool fullLan, bool exit)
    {
        lock (_gate)
        {
            if (!IsCurrent(run)) return "not-consented";
            var a = _active!;
            if (index <= a.LastIndex) return "stale";
            if (a.StepRunning) return "busy";
            if (a.Steps >= MaxSteps) return "too-many-steps";
            if (fullLan && !a.FullLan) return "full-lan-not-allowed";
            if (exit && !a.Exit) return "exit-not-allowed";
            a.LastIndex = index;
            a.Steps++;
            a.StepRunning = true;
            a.LastActivity = _now();
            return null;
        }
    }

    public void EndStep(string? run)
    {
        lock (_gate)
        {
            if (_active is { } a && a.Run == run)
            {
                a.StepRunning = false;
                a.LastActivity = _now();
            }
        }
    }

    /// <summary>开始结束测试：马上撤销同意并保留恢复状态，返回原设置。收尾完成后必须调用 CompleteEnd；重复或其他测试的结束返回 false。</summary>
    public bool End(string? run, out EasyTierOptions? original)
    {
        lock (_gate)
        {
            original = null;
            if (_active is not { } a || a.Run != run) return false;
            original = a.Original;
            _active = null;
            _restoringRun = a.Run;
            return true;
        }
    }

    /// <summary>原设置已经恢复（或恢复失败后已停止临时会话），结束通知也已发出：重新允许请求。</summary>
    internal void CompleteEnd(string? run)
    {
        lock (_gate)
            if (_restoringRun == run) _restoringRun = null;
    }

    /// <summary>同意过但太久没收到下一步，或者整个测试超过了最长时间：返回 true 和这次测试的编号（调用方用 End 恢复）。</summary>
    public bool IsIdle(out string run)
    {
        lock (_gate)
        {
            run = _active?.Run ?? "";
            return _active is { } a && (!a.StepRunning && _now() - a.LastActivity > IdleLimit || _now() - a.Started > MaxDuration);
        }
    }

    private bool IsCurrent(string? run) => _active is { } a && run is not null && a.Run == run
        && _now() - a.LastActivity <= IdleLimit && _now() - a.Started <= MaxDuration;
}
