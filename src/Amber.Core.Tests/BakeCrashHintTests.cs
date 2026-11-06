using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>烘焙成功但游戏没进主界面就崩溃时，提示“不烘焙再启动”。</summary>
public sealed class BakeCrashHintTests
{
    [Theory]
    [InlineData(true, false, 1, false, false, true)]   // 烘焙后没进主界面就崩了
    [InlineData(true, false, 1, true, true, true)]     // 只是兜底时间“猜”进入的，也算没进
    [InlineData(true, false, 1, true, false, false)]   // 进过主界面后才崩：不怪烘焙
    [InlineData(true, false, 0, false, false, false)]  // 正常退出
    [InlineData(true, true, 1, false, false, false)]   // 玩家自己结束的
    [InlineData(false, false, 1, false, false, false)] // 没用烘焙
    public void OnlyBakedRunsThatDieBeforeTheMenuGetTheHint(bool baked, bool stopped, int code, bool inGame, bool guessed, bool expected) =>
        Assert.Equal(expected, HomeViewModel.LikelyBakeCrash(baked, stopped, code, inGame, guessed));

    [Fact]
    public void RetryRequestKeepsTargetAndSkipsBake()
    {
        var retry = new LaunchRequest("x", JoinServer: "example.org") with { SkipBake = true };
        Assert.Equal(new LaunchRequest("x", "example.org", null, SkipBake: true), retry);
        Assert.False(new LaunchRequest("x").SkipBake);
    }
}
