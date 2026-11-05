using System.Runtime.CompilerServices;

namespace Amber.Core.Tests;

/// <summary>测试里建 LauncherService 时，Amber 的设置和账号放到临时目录，不碰真实的数据目录。</summary>
internal static class TestDataDir
{
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amber-test-data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("AMBER_DATA_DIR", Path);
    }
}
