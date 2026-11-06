using System.Globalization;
using System.IO.Compression;
using System.Text;
using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

public sealed class GameLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-language-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Languages =
        ["en_us", "en_nz", "zh_cn", "zh_tw", "zh_hk", "ja_jp", "de_de", "fr_fr", "pt_br", "pt_pt", "no_no"];

    public GameLanguageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("zh-NZ", "zh_cn")]
    [InlineData("zh-Hans-NZ", "zh_cn")]
    [InlineData("zh-TW", "zh_tw")]
    [InlineData("zh-HK", "zh_hk")]
    [InlineData("zh-Hant-HK", "zh_tw")]
    [InlineData("ja", "ja_jp")]
    [InlineData("en-NZ", "en_nz")]
    [InlineData("en-IE", "en_us")]
    [InlineData("de-DE", "de_de")]
    [InlineData("fr-CA", "fr_fr")]
    [InlineData("pt-PT", "pt_pt")]
    [InlineData("nb-NO", "no_no")]
    [InlineData("th-TH", "en_us")]
    public void MatchesOnlyLanguagesProvidedByTheGame(string system, string expected) =>
        Assert.Equal(expected, GameLanguage.Match(CultureInfo.GetCultureInfo(system), Languages));

    [Fact]
    public void OldLanguageFileNamesKeepTheirCase() =>
        Assert.Equal("zh_CN", GameLanguage.Match(CultureInfo.GetCultureInfo("zh-CN"), ["en_US", "zh_CN"]));

    [Fact]
    public void AppendsLanguageWithoutChangingSoundOrOtherOptions()
    {
        var path = Path.Combine(_root, "options.txt");
        var original = "soundCategory_master:0.0\r\nrenderDistance:12";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        GameLanguage.WriteDefault(_root, "zh_cn");
        Assert.StartsWith(original + Environment.NewLine, File.ReadAllText(path));
        Assert.Contains("lang:zh_cn", File.ReadAllLines(path));
    }

    [Fact]
    public void ExistingPlayerLanguageIsPreservedByteForByte()
    {
        var path = Path.Combine(_root, "options.txt");
        var original = Encoding.UTF8.GetBytes("lang:ja_jp\r\nsoundCategory_master:0.0\r\n");
        File.WriteAllBytes(path, original);
        GameLanguage.WriteDefault(_root, "zh_cn");
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void EmptyLanguageGetsADefault()
    {
        var path = Path.Combine(_root, "options.txt");
        File.WriteAllText(path, "lang:   \nsoundCategory_master:0.0\n");
        GameLanguage.WriteDefault(_root, "zh_cn");
        Assert.Equal("lang:zh_cn", File.ReadAllLines(path).Last());
    }

    [Fact]
    public void ConcurrentPreparationWritesOneLanguageWithoutBom()
    {
        Parallel.For(0, 16, _ => GameLanguage.WriteDefault(_root, "zh_cn"));
        var path = Path.Combine(_root, "options.txt");
        Assert.Equal(["lang:zh_cn"], File.ReadAllLines(path));
        Assert.Equal((byte)'l', File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void UsesAssetIndexAndInheritedClientJarBeforeFirstLaunch()
    {
        var directory = new GameDirectory(_root);
        Directory.CreateDirectory(directory.VersionDir("vanilla"));
        using (var jar = ZipFile.Open(directory.VersionJarPath("vanilla"), ZipArchiveMode.Create))
            jar.CreateEntry("assets/minecraft/lang/en_us.json");
        Directory.CreateDirectory(Path.GetDirectoryName(directory.AssetIndexPath("test"))!);
        File.WriteAllText(directory.AssetIndexPath("test"),
            """{"objects":{"minecraft/lang/zh_cn.json":{"hash":"0000000000000000000000000000000000000000","size":0}}}""");
        var isolated = Path.Combine(_root, "versions", "fabric");
        Directory.CreateDirectory(isolated);
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-NZ");
            GameLanguage.Prepare(directory, new VersionJson
            {
                Id = "fabric",
                Jar = "vanilla",
                AssetIndex = new AssetIndexRef { Id = "test" },
            }, isolated);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
        Assert.Contains("lang:zh_cn", File.ReadAllLines(Path.Combine(isolated, "options.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "options.txt")));
    }
}
