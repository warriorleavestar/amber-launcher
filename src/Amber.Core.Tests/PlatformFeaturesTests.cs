using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.RemoteControl;

namespace Amber.Core.Tests;

public sealed class PlatformFeaturesTests
{
    [Fact]
    public void MatchesWhatThisPlatformCanDo()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PlatformFeatures.FullLan);
        // 出口建立在完整局域网之上：不能出现“有出口没完整局域网”。
        Assert.True(!PlatformFeatures.ExitNode || PlatformFeatures.FullLan);
        Assert.Equal(RemotePlatform.Supported, PlatformFeatures.RemoteControl);
        Assert.Equal(NoiseHandshake.HybridSupported, PlatformFeatures.ExtremeEncryption);
    }

    [Fact]
    public void MatrixWithoutFullLanHasNoFullLanOrExitSteps()
    {
        // 全条件测试：不允许完整局域网时（比如一方是 Mac/Linux），计划里不会有必然失败的完整局域网和出口步骤。
        var plan = TestMatrix.Plan(new MatrixInputs([], [], 3, true, PlatformFeatures.FullLan && false));
        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, s => s.FullLan || s.Exit);
    }
}
