using System.Text.RegularExpressions;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>
/// 多语言的检查：界面代码里没有写死的中文；用到的键都在简体中文表里；每种语言的键齐全、占位符一致。
/// </summary>
public partial class LocalizationTests
{
    private static readonly string Src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>开发用的自检文件不翻译。</summary>
    private static readonly string[] Excluded = ["SelfTest.cs", "LayoutCheck.cs"];

    // 汉字、假名，以及中日文标点（、。「」……全角符号）：写死在代码里的标点换语言后也会留在文字中间。
    [GeneratedRegex(@"[一-鿿぀-ヿ　-〿＀-￯]")]
    private static partial Regex Cjk();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CsComments();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComments();

    [GeneratedRegex(@"\bL\.[TF]\(\s*""([^""]+)""")]
    private static partial Regex CsKey();

    [GeneratedRegex(@"\{l:T\s+([^}\s]+)\s*\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static IEnumerable<string> UiFiles() =>
        Directory.EnumerateFiles(Path.Combine(Src, "Amber.App"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !Excluded.Contains(Path.GetFileName(f)));

    private static string StripComments(string file, string text) =>
        file.EndsWith(".cs", StringComparison.Ordinal) ? CsComments().Replace(text, "") : XmlComments().Replace(text, "");

    [Fact]
    public void NoHardCodedChineseInUi()
    {
        var offenders = new List<string>();
        foreach (var file in UiFiles())
        {
            var lines = StripComments(file, File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Cjk().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(Src, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "界面里还有写死的中文：\n" + string.Join("\n", offenders.Take(40)));
    }

    /// <summary>Core 里故意保留的中文：和游戏日志、比较用的常量对得上的文字。</summary>
    private static readonly string[] CoreAllowed =
    [
        "public const string MatchText = \"匹配\";",
        "在端口",
        // 语言选单里每种语言用它自己的写法显示。
        "(\"zh-Hans\", \"简体中文\")",
        "(\"zh-Hant\", \"繁體中文\")",
        "(\"ja\", \"日本語\")",
    ];

    [Fact]
    public void NoHardCodedChineseInCore()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Src, "Amber.Core"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lines = StripComments(file, File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Cjk().IsMatch(lines[i]) && !CoreAllowed.Any(a => lines[i].Contains(a, StringComparison.Ordinal)))
                    offenders.Add($"{Path.GetRelativePath(Src, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "Core 里还有写死的中文：\n" + string.Join("\n", offenders.Take(40)));
    }

    [Fact]
    public void EveryUsedKeyExists()
    {
        var source = L.Load(L.Source);
        var missing = new List<string>();
        foreach (var file in UiFiles().Concat(Directory.EnumerateFiles(Path.Combine(Src, "Amber.Core"), "*.cs", SearchOption.AllDirectories)))
        {
            var text = StripComments(file, File.ReadAllText(file));
            foreach (Match m in CsKey().Matches(text).Concat(XamlKey().Matches(text)))
                if (!source.ContainsKey(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        }
        Assert.True(missing.Count == 0, "简体中文表里没有这些键：\n" + string.Join("\n", missing.Distinct()));
    }

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void TranslationsAreCompleteAndKeepPlaceholders(string code)
    {
        var source = L.Load(L.Source);
        var table = L.Load(code);
        var missing = source.Keys.Where(k => !table.ContainsKey(k)).ToList();
        var extra = table.Keys.Where(k => !source.ContainsKey(k)).ToList();
        var placeholderMismatch = source.Keys.Where(table.ContainsKey)
            .Where(k => !Placeholders(source[k]).SetEquals(Placeholders(table[k])))
            .Select(k => $"{k}: “{source[k]}” → “{table[k]}”").ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0 && placeholderMismatch.Count == 0,
            $"{code}：缺 {missing.Count} 个（{string.Join(", ", missing.Take(10))}），多 {extra.Count} 个（{string.Join(", ", extra.Take(10))}），占位符不一致：\n{string.Join("\n", placeholderMismatch.Take(10))}");
    }

    private static HashSet<string> Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void LanguageFollowsSystemAndFallsBack()
    {
        Assert.Equal("zh-Hans", L.FromCulture(new System.Globalization.CultureInfo("zh-CN")));
        Assert.Equal("zh-Hant", L.FromCulture(new System.Globalization.CultureInfo("zh-TW")));
        Assert.Equal("zh-Hant", L.FromCulture(new System.Globalization.CultureInfo("zh-HK")));
        Assert.Equal("ja", L.FromCulture(new System.Globalization.CultureInfo("ja-JP")));
        Assert.Equal("en", L.FromCulture(new System.Globalization.CultureInfo("de-DE")));
        // 不存在的键原样返回，漏翻的地方一眼能看出来。
        Assert.Equal("no.such.key", L.T("no.such.key"));
    }
}
