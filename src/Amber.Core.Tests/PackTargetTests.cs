using Amber.Core.Content;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>R4：整合包不能提供启动器自己的文件（实例设置、版本文件、natives），也不能带 JVM 参数。</summary>
public sealed class PackTargetTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "amber-pack-target");
    private static readonly string VersionDir = Path.Combine(Root, "versions", "Pack");

    // 版本隔离时工作目录就是版本目录。
    private static readonly ModpackService.PackTarget Isolated = new(VersionDir, VersionDir, "Pack");
    private static readonly ModpackService.PackTarget Shared = new(Root, VersionDir, "Pack");

    [Theory]
    [InlineData("amber-instance.json")]
    [InlineData("AMBER-INSTANCE.JSON")]
    [InlineData("config/amber-instance.json")]
    [InlineData("./amber-instance.json")]
    [InlineData("mods/../amber-instance.json")]
    [InlineData("amber-instance.json.")]
    [InlineData("amber-instance.json ")]
    [InlineData("Pack.json")]
    [InlineData("pack.JSON")]
    [InlineData("Pack.jar")]
    [InlineData("natives/lwjgl.dll")]
    [InlineData("NATIVES/x/y.dll")]
    public void LauncherFilesAreSkipped(string relative) => Assert.Null(Isolated.Resolve(relative));

    [Theory]
    [InlineData("amber-instance.json")]
    [InlineData("versions/Pack/amber-instance.json")]
    [InlineData("versions/Pack/Pack.json")]
    [InlineData("versions/Pack/Pack.jar")]
    [InlineData("versions/Pack/natives/a.dll")]
    public void LauncherFilesAreSkippedInSharedDir(string relative) => Assert.Null(Shared.Resolve(relative));

    [Theory]
    [InlineData("amber-instance.json::$DATA")]
    [InlineData("amber-instance.json:hidden")]
    [InlineData("AMBER-~1.JSO")]
    [InlineData("config/PACK~1.JSO")]
    [InlineData("../evil.txt")]
    public void TrickyNamesAreRejected(string relative) => Assert.Throws<InvalidDataException>(() => Isolated.Resolve(relative));

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/pack.json")]
    [InlineData("options.txt")]
    [InlineData("mods/sodium-fabric-0.5.8~1.20.jar")]
    [InlineData("config/natives/settings.toml")]
    public void NormalFilesAreKept(string relative) => Assert.StartsWith(VersionDir, Isolated.Resolve(relative));

    [Fact]
    public void MultiMcJvmArgsAreNotImported()
    {
        var settings = new InstanceSettings();
        ModpackService.ApplyMultiMcCfg(new Dictionary<string, string>
        {
            ["OverrideJavaArgs"] = "true",
            ["JvmArgs"] = "-XX:OnOutOfMemoryError=calc.exe",
            ["OverrideMemory"] = "true",
            ["MaxMemAlloc"] = "6144",
        }, settings);
        Assert.Null(settings.JvmArgs);
        Assert.Null(settings.PreLaunchCommand);
        Assert.Null(settings.JavaPath);
        Assert.Equal(6144, settings.MaxMemoryMb);
    }
}
