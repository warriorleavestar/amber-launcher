namespace Amber.Core;

/// <summary>用于界面和测试在用户操作前说清平台限制；后续补齐某项时只改这里的能力判定。</summary>
public static class PlatformFeatures
{
    public static bool FullLan => OperatingSystem.IsWindows();
    public static bool FullTrafficExit => OperatingSystem.IsWindows();
    public static bool RemoteControl => OperatingSystem.IsWindows();
}
