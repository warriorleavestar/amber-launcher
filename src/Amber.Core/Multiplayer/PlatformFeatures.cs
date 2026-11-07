using Amber.Core.Multiplayer.Transport;
using Amber.Core.RemoteControl;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 这台电脑上哪些联机功能能用。界面按它禁用选项并说明原因，全条件测试按它排除必然失败的步骤。
/// Mac/Linux 每补齐一项，就把对应的值改成 true（见 docs/platform-porting-guide.md 第 5 节）。
/// </summary>
public static class PlatformFeatures
{
    /// <summary>完整局域网（虚拟网卡 + 广播转发）：现在只有 Windows 有特权守护和网卡驱动。</summary>
    public static bool FullLan => OperatingSystem.IsWindows();

    /// <summary>全部流量出口：建立在完整局域网之上，另外要 WFP 防泄漏规则。</summary>
    public static bool ExitNode => OperatingSystem.IsWindows();

    /// <summary>远程控制（截屏 + 注入）。</summary>
    public static bool RemoteControl => RemotePlatform.Supported;

    /// <summary>极端加密（ML-KEM-768 混合握手），看系统加密库是否支持。</summary>
    public static bool ExtremeEncryption => NoiseHandshake.HybridSupported;
}
