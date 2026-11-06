using Amber.App.Views.Controls;

namespace Amber.Core.Tests;

public sealed class BakeNoticeTests
{
    // 与现有真实控件测试一致：只有 Windows 测试线程初始化了 Avalonia 渲染平台。
    private static void OnUi(Action action)
    {
        if (OperatingSystem.IsWindows()) UiTestThread.Run(action);
    }

    [Fact]
    public void RetryIsSingleUseEvenWhenCallbackReenters() => OnUi(() =>
    {
        var host = new BakeNoticeState();
        var calls = 0;
        BakeNotice? notice = null;
        notice = host.Show("instance", () => { calls++; notice!.RetryCommand.Execute(null); });
        notice.RetryCommand.Execute(null);
        notice.RetryCommand.Execute(null);
        Assert.Equal(1, calls);
        Assert.Empty(host.Notices);
    });

    [Fact]
    public void DismissedNoticeCannotRetryOrDismissItsReplacement() => OnUi(() =>
    {
        var host = new BakeNoticeState();
        var calls = new List<string>();
        var old = host.Show("old", () => calls.Add("old"));
        old.DismissCommand.Execute(null);
        var current = host.Show("current", () => calls.Add("current"));
        old.DismissCommand.Execute(null);
        old.RetryCommand.Execute(null);
        Assert.Same(current, Assert.Single(host.Notices));
        Assert.Same(current, host.Current);
        current.RetryCommand.Execute(null);
        Assert.Equal(["current"], calls);
    });

    [Fact]
    public void OverflowAndWindowCleanupInvalidateOnlyTheirOwnCallbacks() => OnUi(() =>
    {
        var host = new BakeNoticeState();
        var calls = new List<int>();
        var all = Enumerable.Range(0, 4).Select(i => host.Show(i.ToString(), () => calls.Add(i))).ToArray();
        Assert.Equal(3, host.Notices.Count);
        Assert.Same(all[3], host.Current);
        all[0].RetryCommand.Execute(null);
        Assert.Empty(calls);
        all[2].RetryCommand.Execute(null);
        Assert.Equal([2], calls);
        Assert.Same(all[3], host.Current);
        all[3].RetryCommand.Execute(null);
        Assert.Same(all[1], host.Current);
        host.Clear();
        foreach (var notice in all) notice.RetryCommand.Execute(null);
        Assert.Empty(host.Notices);
        Assert.Equal([2, 3], calls);
        Assert.False(host.HasNotice);
    });
}
