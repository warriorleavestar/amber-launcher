using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>参数里带的路径：表示不了时换成英文联接（这里用假的联接函数，不真的建联接）。</summary>
public class AsciiPathArgumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-ascii-" + Guid.NewGuid().ToString("N"), "外置登录");
    private readonly string _jar;

    public AsciiPathArgumentTests()
    {
        Directory.CreateDirectory(_dir);
        _jar = Path.Combine(_dir, "authlib-injector.jar");
        File.WriteAllText(_jar, "x");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, true);

    private static bool AsciiOnly(string text) => text.All(c => c < 128);

    private string Alias(string dir) => dir.Equals(_dir, StringComparison.OrdinalIgnoreCase) ? @"C:\AmberLinks\a0123456789" : dir;

    [Fact]
    public void JavaAgentWithOptions()
    {
        var arg = $"-javaagent:{_jar}=https://skin.example.org/api/yggdrasil";
        Assert.Equal(@"-javaagent:C:\AmberLinks\a0123456789\authlib-injector.jar=https://skin.example.org/api/yggdrasil", AsciiPaths.MapArgument(arg, AsciiOnly, Alias));
    }

    [Fact]
    public void SystemPropertyAndWholePath()
    {
        Assert.Equal(@"-Dlog4j.configurationFile=C:\AmberLinks\a0123456789\authlib-injector.jar",
            AsciiPaths.MapArgument("-Dlog4j.configurationFile=" + _jar, AsciiOnly, Alias));
        Assert.Equal(@"C:\AmberLinks\a0123456789", AsciiPaths.MapArgument(_dir, AsciiOnly, Alias));
    }

    [Fact]
    public void LeavesOtherArgumentsAlone()
    {
        // 能表示的、不是路径的（玩家名、服务器名）、不存在的路径都原样。
        Assert.Equal("-Xmx4G", AsciiPaths.MapArgument("-Xmx4G", AsciiOnly, Alias));
        Assert.Equal("史蒂夫", AsciiPaths.MapArgument("史蒂夫", AsciiOnly, Alias));
        Assert.Equal("-Dx=中文", AsciiPaths.MapArgument("-Dx=中文", AsciiOnly, Alias));
        var missing = Path.Combine(_dir, "不存在.jar");
        Assert.Equal("-javaagent:" + missing, AsciiPaths.MapArgument("-javaagent:" + missing, AsciiOnly, Alias));
        // 当前代码页能表示（比如中文系统）时什么都不改。
        Assert.Equal("-javaagent:" + _jar, AsciiPaths.MapArgument("-javaagent:" + _jar, _ => true, Alias));
    }
}
