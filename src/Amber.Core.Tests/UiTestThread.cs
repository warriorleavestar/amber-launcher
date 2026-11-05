using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia;

namespace Amber.Core.Tests;

/// <summary>
/// 测试里要用 Avalonia 的东西（Dispatcher.UIThread、文字排版）都放到这一个 STA 线程上跑。
/// Avalonia 的界面线程是进程级的、只认第一个用到它的线程，各测试各开线程会互相抢（"a different thread owns it"）。
/// 第一次用时在这个线程上初始化渲染平台（不开窗口、不启动启动器）。
/// </summary>
internal static class UiTestThread
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly Lazy<Thread> Worker = new(Start);

    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            if (OperatingSystem.IsWindows())
                AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
            foreach (var action in Work.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "Avalonia test UI thread" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>在界面线程上运行 action 并等它结束；超时算失败（多半是布局死循环）。</summary>
    public static void Run(Action action, int timeoutSeconds = 30)
    {
        _ = Worker.Value;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Work.Add(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(timeoutSeconds)), "界面线程上的测试超时，可能存在布局循环");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
