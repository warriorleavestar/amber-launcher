using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia;

namespace Amber.Core.Tests;

/// <summary>
/// 测试里要用 Avalonia 的东西（Dispatcher.UIThread、文字排版）都放到这一个 STA 线程上跑。
/// Avalonia 的界面线程是进程级的、只认第一个用到它的线程；别的测试（比如换语言时 Post 到界面线程）可能先碰到它，
/// 所以测试模块一加载就在这个线程上初始化渲染平台（不开窗口、不启动启动器），初始化完才开始跑测试。
/// </summary>
internal static class UiTestThread
{
    private static readonly BlockingCollection<Action> Work = [];
    private static Exception? _setupFailure;

    [ModuleInitializer]
    internal static void Start()
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
            }
            catch (Exception ex) { _setupFailure = ex; }
            finally { ready.Set(); }
            foreach (var action in Work.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "Avalonia test UI thread" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>在界面线程上运行 action 并等它结束；超时算失败（多半是布局死循环）。</summary>
    public static void Run(Action action, int timeoutSeconds = 30)
    {
        if (_setupFailure is not null) ExceptionDispatchInfo.Capture(_setupFailure).Throw();
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
