using System.IO.Compression;
using System.Reflection;
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
    [InlineData(".amber-ul/jay-bake.json")]
    [InlineData(".AMBER-UL/state.json")]
    public void LauncherFilesAreSkipped(string relative) => Assert.Null(Isolated.Resolve(relative));

    [Theory]
    [InlineData("amber-instance.json")]
    [InlineData("versions/Pack/amber-instance.json")]
    [InlineData("versions/Pack/Pack.json")]
    [InlineData("versions/Pack/Pack.jar")]
    [InlineData("versions/Pack/natives/a.dll")]
    public void LauncherFilesAreSkippedInSharedDir(string relative) => Assert.Null(Shared.Resolve(relative));

    /// <summary>
    /// 可疑的名字跳过这个文件（并通知），不让整个导入失败：备用数据流、8.3 短名（包括结尾带点或空格、Windows 会去掉的写法）、
    /// 设备名、路径中间某一段是启动器文件。
    /// </summary>
    [Theory]
    [InlineData("amber-instance.json::$DATA")]
    [InlineData("amber-instance.json:hidden")]
    [InlineData("AMBER-~1.JSO")]
    [InlineData("AMBER-~1.JSO.")]
    [InlineData("AMBER-~1.JSO ")]
    [InlineData("AMBER-~1.JSO. . ")]
    [InlineData("config/PACK~1.JSO")]
    [InlineData("PACK~1.JSO ")]
    [InlineData("PACK~1.JSO.")]
    [InlineData("amber-instance.json/x")]
    [InlineData("config/amber-instance.json/x")]
    [InlineData("Pack.json/x")]
    [InlineData("Pack.json./x")]
    [InlineData("CON")]
    [InlineData("config/nul.txt")]
    [InlineData("mods/COM1.jar")]
    [InlineData("lpt\u00b9")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("mods/a?.jar")]
    [InlineData("...")]
    public void TrickyNamesAreSkipped(string relative)
    {
        var skipped = new List<string>();
        var target = Isolated with { OnSkip = skipped.Add };
        Assert.Null(target.Resolve(relative));
        Assert.Equal([relative], skipped);
    }

    /// <summary>真正跳出目录的路径才抛异常。</summary>
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("mods/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("\\evil.txt")]
    public void EscapesAreRejected(string relative) => Assert.Throws<InvalidDataException>(() => Isolated.Resolve(relative));

    /// <summary>规范化以后和 Windows 实际写到的位置一致：结尾的点和空格去掉。</summary>
    [Fact]
    public void TrailingDotsAndSpacesAreNormalized() =>
        Assert.Equal(Path.Combine(VersionDir, "config", "a.txt"), Isolated.Resolve("config. /a.txt.. "));

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/pack.json")]
    [InlineData("options.txt")]
    [InlineData("mods/sodium-fabric-0.5.8~1.20.jar")]
    [InlineData("config/natives/settings.toml")]
    public void NormalFilesAreKept(string relative) => Assert.StartsWith(VersionDir, Isolated.Resolve(relative));

    /// <summary>版本名带非 ASCII 字符时，NFD 写法（macOS 上和 NFC 是同一个文件）也算版本文件。</summary>
    [Theory]
    [InlineData("Cafe\u0301.json")]
    [InlineData("CAFE\u0301.JAR")]
    [InlineData("Caf\u00e9.json")]
    public void NfdAliasOfVersionFileIsSkipped(string relative)
    {
        var dir = Path.Combine(Root, "versions", "Caf\u00e9");
        Assert.Null(new ModpackService.PackTarget(dir, dir, "Caf\u00e9").Resolve(relative));
    }

    /// <summary>前缀后面多一个斜杠（“overrides//mods/a.jar”）照常释放，不让整个导入失败。</summary>
    [Fact]
    public void ExtraSlashAfterPrefixIsTolerated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-pack-slash-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("overrides//mods/a.jar").Open()))
                writer.Write("x");
            stream.Position = 0;
            using var read = new ZipArchive(stream, ZipArchiveMode.Read);
            var extract = typeof(ModpackService).GetMethod("ExtractPrefix", BindingFlags.NonPublic | BindingFlags.Static)!;
            extract.Invoke(null, [read, "overrides/", new ModpackService.PackTarget(dir, dir, "Pack"), null, null]);
            Assert.True(File.Exists(Path.Combine(dir, "mods", "a.jar")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

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
