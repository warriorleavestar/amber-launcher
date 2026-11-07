using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

public class LegacyForgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-oldforge-" + Guid.NewGuid().ToString("N"));

    public LegacyForgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("1.5.2", true)]
    [InlineData("1.5", true)]
    [InlineData("1.4.7", true)]
    [InlineData("1.2.5", true)]
    [InlineData("1.1", true)]
    [InlineData("1.6.1", false)]
    [InlineData("1.6.4", false)]
    [InlineData("1.7.10", false)]
    [InlineData("1.21.1", false)]
    [InlineData("26.3", false)]
    public void JarModEra(string mc, bool expected) => Assert.Equal(expected, LegacyForge.IsJarModEra(mc));

    /// <summary>手工拼一个最小的类文件：常量池里有几个 UTF-8 字符串和一个 long（占两个槽）。</summary>
    private static byte[] FakeClass(params string[] strings)
    {
        using var m = new MemoryStream();
        void U2(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); m.Write(b); }
        m.Write([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 50]);
        U2(strings.Length + 3);   // 常量个数 + 1，long 占 2
        foreach (var s in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            m.WriteByte(1);
            U2(bytes.Length);
            m.Write(bytes);
        }
        m.WriteByte(5);
        m.Write(new byte[8]);
        return m.ToArray();
    }

    [Fact]
    public void Utf8ConstantsReadsPoolInOrder()
    {
        var data = FakeClass("a.jar", "b.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51");
        Assert.Equal(["a.jar", "b.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"], LegacyForge.Utf8Constants(data));
        Assert.Throws<InvalidDataException>(() => LegacyForge.Utf8Constants([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    private string Zip(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(data);
        }
        return path;
    }

    [Fact]
    public void ReadsFmlLibrariesAndDeobfuscationData()
    {
        var forge = Zip("forge.zip",
            ("cpw/mods/fml/relauncher/CoreFMLLibraries.class", FakeClass("*http://files.minecraftforge.net/fmllibs/%s", "argo-small-3.2.jar", "guava-14.0-rc3.jar",
                "58912ea2858d168c50781f956fa5b59f0f7c6b51", "931ae21fa8014c3ce686aaa621eae565fefb1a6a", "deobfuscationDataHash")),
            ("fmlversion.properties", "fmlbuild.mcversion=1.5.2\nfmlbuild.deobfuscation.hash=446e55cd986582c70fcf12cb27bc00114c5adfd9\n"u8.ToArray()));
        var libs = LegacyForge.ReadFmlLibraries(forge, "1.5.2");
        Assert.Equal(
        [
            new FmlLibrary("argo-small-3.2.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"),
            new FmlLibrary("guava-14.0-rc3.jar", "931ae21fa8014c3ce686aaa621eae565fefb1a6a"),
            new FmlLibrary("deobfuscation_data_1.5.2.zip", "446e55cd986582c70fcf12cb27bc00114c5adfd9"),
        ], libs);

        // 1.2.5 及更早没有 CoreFMLLibraries，不需要库。
        Assert.Empty(LegacyForge.ReadFmlLibraries(Zip("old.zip", ("mod_MinecraftForge.info", [1])), "1.2.5"));
        // 文件名和哈希对不上数时不要乱配。
        var broken = Zip("broken.zip", ("cpw/mods/fml/relauncher/CoreFMLLibraries.class", FakeClass("a.jar", "b.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51")));
        Assert.Throws<InvalidDataException>(() => LegacyForge.ReadFmlLibraries(broken, "1.4.7"));
    }

    [Fact]
    public void SourcesEndWithArchiveFallback()
    {
        var sources = LegacyForge.Sources("asm-all-4.0.jar").ToList();
        Assert.StartsWith("https://repo1.maven.org/", sources[0]);
        Assert.Contains("web.archive.org", sources[^1]);
        Assert.Single(LegacyForge.Sources("unknown-1.0.jar"));
    }

    [Fact]
    public void MergeJarStripsSignaturesAndForgeWins()
    {
        var vanilla = Zip("vanilla.jar",
            ("META-INF/MANIFEST.MF", "x"u8.ToArray()), ("META-INF/MOJANG_C.SF", "sig"u8.ToArray()),
            ("net/minecraft/client/Minecraft.class", "vanilla"u8.ToArray()), ("abc.class", "vanilla-abc"u8.ToArray()));
        var forge = Zip("forge.zip",
            ("META-INF/FORGE.SF", "sig"u8.ToArray()), ("abc.class", "forge-abc"u8.ToArray()), ("forge_logo.png", [9]));
        var target = Path.Combine(_dir, "out", "merged.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        LegacyForge.MergeJar(vanilla, forge, target);
        using var zip = ZipFile.OpenRead(target);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("META-INF/", StringComparison.Ordinal));
        string Read(string n) { using var r = new StreamReader(zip.GetEntry(n)!.Open()); return r.ReadToEnd(); }
        Assert.Equal("forge-abc", Read("abc.class"));
        Assert.Equal("vanilla", Read("net/minecraft/client/Minecraft.class"));
        Assert.NotNull(zip.GetEntry("forge_logo.png"));
    }

    [Theory]
    [InlineData("2013-04-25T15:45:00+00:00", null, true)]
    [InlineData("2012-03-29T22:00:00+00:00", null, true)]
    [InlineData("2013-06-25T13:08:56+00:00", null, false)]
    [InlineData("2014-05-14T17:29:23+00:00", null, false)]
    [InlineData("2014-05-14T17:29:23+00:00", "1.5.2-7.8.1.738", true)]
    public void PreLaunchwrapperHome(string released, string? amberForge, bool expected) =>
        Assert.Equal(expected, GameLauncher.IsPreLaunchwrapperHome(new VersionJson { ReleaseTime = released, AmberForge = amberForge }));

    [Fact]
    public void LegacyHomeLinksTheGamesMinecraftDirectoryToTheInstance()
    {
        // 测试只在临时目录里建链接，不碰用户真实的 minecraft 目录。
        var instance = Path.Combine(_dir, "实例 1");
        Directory.CreateDirectory(instance);
        var root = Path.Combine(_dir, "legacyhome");
        var home = AsciiPaths.LegacyHome(instance, root, null);
        if (home is null && OperatingSystem.IsWindows()) return; // 没有权限建联接的 Windows 环境
        Assert.NotNull(home);
        Assert.StartsWith(root, home);
        var link = Path.Combine(home, AsciiPaths.LegacyMinecraftDirectory());
        if (OperatingSystem.IsMacOS()) Assert.EndsWith(Path.Combine("Library", "Application Support", "minecraft"), link);
        else Assert.EndsWith(".minecraft", link);
        // 游戏写到“家目录里的 minecraft”就是写到实例目录。
        File.WriteAllText(Path.Combine(link, "options.txt"), "lang:zh_CN");
        Assert.True(File.Exists(Path.Combine(instance, "options.txt")));
        // 第二次直接复用；实例换了位置时换新链接，旧目标不受影响。
        Assert.Equal(home, AsciiPaths.LegacyHome(instance, root, null));
        Assert.True(File.Exists(Path.Combine(instance, "options.txt")));
    }

    [Fact]
    public void LegacyHomeNeverReplacesARealDirectory()
    {
        var instance = Path.Combine(_dir, "inst");
        Directory.CreateDirectory(instance);
        var root = Path.Combine(_dir, "legacyhome");
        var first = AsciiPaths.LegacyHome(instance, root, null);
        if (first is null) return;
        var link = Path.Combine(first, AsciiPaths.LegacyMinecraftDirectory());
        new DirectoryInfo(link).Delete();
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "keep.txt"), "x");
        Assert.Null(AsciiPaths.LegacyHome(instance, root, null));
        Assert.True(File.Exists(Path.Combine(link, "keep.txt")));
    }

    [Fact]
    public void LegacyHomeIsPassedToTheGameProcess()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("java");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add("a.jar");
        psi.ArgumentList.Add("net.minecraft.client.Minecraft");
        GameLauncher.ApplyLegacyHome(psi, "/tmp/legacy home");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("/tmp/legacy home", psi.Environment["APPDATA"]);
            Assert.Equal(3, psi.ArgumentList.Count);
        }
        else
        {
            // 必须在主类前面，作为一个完整参数（路径里有空格也不拆开）。
            Assert.Equal("-Duser.home=/tmp/legacy home", psi.ArgumentList[0]);
            Assert.Equal("net.minecraft.client.Minecraft", psi.ArgumentList[^1]);
        }
    }

    [Fact]
    public void EnsureLibrariesCopiesAndReplacesBadCopies()
    {
        var root = Path.Combine(_dir, "root");
        var cache = LegacyForge.FmlCacheDir(root);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "a.jar"), "good");
        var sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData("good"u8));
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(Path.Combine(work, "lib"));
        File.WriteAllText(Path.Combine(work, "lib", "a.jar"), "<html>404</html>");   // FML 从死地址下回来的“文件”
        LegacyForge.EnsureLibraries(root, work, [new FmlLibrary("a.jar", sha1)], null);
        Assert.Equal("good", File.ReadAllText(Path.Combine(work, "lib", "a.jar")));
    }

    [Fact]
    public void CrashRuleForDeadFmllibs()
    {
        var log = "java.lang.RuntimeException: The downloaded file scala-library.jar has an invalid checksum e04c5335922c5e457f0a7cd62c93c4a7f699f829 (expecting 458d046151ad179c85429ed7420ffb1eaf6ddf85).";
        var first = Amber.Core.Diagnostics.CrashAnalyzer.Analyze(log).Findings[0];
        Assert.Contains("scala-library.jar", first.Title);
    }
}
