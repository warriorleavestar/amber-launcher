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
    private static readonly string[] Excluded = ["SelfTest.cs", "SelfTest.Hotspot.cs", "LayoutCheck.cs", "Spike.cs"];

    // 汉字（含扩展 A）、假名、中日文标点和全角符号，以及中文里常用的 …“”—：写死在代码里的标点换语言后也会留在文字中间。
    [GeneratedRegex(@"[㐀-䶿一-鿿぀-ヿ　-〿＀-￯…“”—]")]
    private static partial Regex Cjk();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CsComments();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComments();

    [GeneratedRegex(@"\bL\.[TFP]\(\s*""([^""]+)""")]
    private static partial Regex CsKey();

    [GeneratedRegex(@"\{l:T\s+([^}\s]+)\s*\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>运行时拼出来的键：崩溃规则 R(模式, "键前缀") 用 前缀.title / 前缀.advice；启动页的 SetIdleStatus("键")。</summary>
    [GeneratedRegex(@"\bR\(@?""(?:[^""]|"""")*"",\s*""([a-z0-9_.]+)""")]
    private static partial Regex CrashRuleKey();

    [GeneratedRegex(@"\bSetIdleStatus\(\s*""([^""]+)""")]
    private static partial Regex IdleStatusKey();

    [GeneratedRegex(@"\bL\.([FP])\(\s*""([^""]+)""")]
    private static partial Regex FormatCall();

    private static IEnumerable<string> UiFiles() =>
        Directory.EnumerateFiles(Path.Combine(Src, "Amber.App"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !Excluded.Contains(Path.GetFileName(f)));

    /// <summary>
    /// 去掉注释，以及只有一个省略号的字符串（截断长文字时用的 "…"，跟语言无关）。
    /// </summary>
    private static string StripComments(string file, string text) =>
        file.EndsWith(".cs", StringComparison.Ordinal) ? CsComments().Replace(text, "").Replace("\"…\"", "\"\"") : XmlComments().Replace(text, "");

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
        foreach (var file in CodeFiles())
        {
            var text = StripComments(file, File.ReadAllText(file));
            foreach (Match m in CsKey().Matches(text).Concat(XamlKey().Matches(text)).Concat(IdleStatusKey().Matches(text)))
                if (!source.ContainsKey(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            foreach (Match m in CrashRuleKey().Matches(text))
                foreach (var suffix in new[] { ".title", ".advice" })
                    if (!source.ContainsKey(m.Groups[1].Value + suffix)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}{suffix}");
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
        var extra = table.Keys.Where(k => !source.ContainsKey(k) && !(PluralBase(k) is { } b && source.ContainsKey(b))).ToList();
        var placeholderMismatch = source.Keys.Where(table.ContainsKey)
            .Where(k => !Placeholders(source[k]).SetEquals(Placeholders(table[k])))
            .Select(k => $"{k}: “{source[k]}” → “{table[k]}”")
            // 单复数变体：用到的占位符不能超出原键的（.one 里可以不写数量）。
            .Concat(table.Keys.Where(k => PluralBase(k) is { } b && source.ContainsKey(b) && !Placeholders(table[k]).IsSubsetOf(Placeholders(source[PluralBase(k)!])))
                .Select(k => $"{k}: “{table[k]}”"))
            .ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0 && placeholderMismatch.Count == 0,
            $"{code}：缺 {missing.Count} 个（{string.Join(", ", missing.Take(10))}），多 {extra.Count} 个（{string.Join(", ", extra.Take(10))}），占位符不一致：\n{string.Join("\n", placeholderMismatch.Take(10))}");
    }

    /// <summary>占位符的完整写法（包括 :F1 这类格式），译文要和原文一模一样。</summary>
    private static HashSet<string> Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    /// <summary>界面、Core 和开发用 CLI 的所有代码（XAML 和 C#），不含 bin / obj。</summary>
    private static IEnumerable<string> CodeFiles() =>
        UiFiles()
            .Concat(Directory.EnumerateFiles(Path.Combine(Src, "Amber.Core"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Src, "Amber.Cli"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>单复数变体（key.one / key.other）：原文表里只有 key，译文可以多出这两种。</summary>
    private static string? PluralBase(string key) =>
        key.EndsWith(".one", StringComparison.Ordinal) ? key[..^4] : key.EndsWith(".other", StringComparison.Ordinal) ? key[..^6] : null;

    private static int MaxIndex(string text) => Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryValueFormats(string code)
    {
        var bad = new List<string>();
        foreach (var (key, value) in L.Load(code))
        {
            var args = Enumerable.Repeat((object)1, MaxIndex(value) + 1).ToArray();
            try { _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, value, args); }
            catch (FormatException e) { bad.Add($"{key}: {e.Message}"); }
        }
        Assert.True(bad.Count == 0, $"{code} 里格式化会出错的文字：\n" + string.Join("\n", bad.Take(20)));
    }

    /// <summary>每处 L.F 调用传的参数个数，够每种语言的译文用（占位符序号 +1）。</summary>
    [Fact]
    public void FormatCallsPassEnoughArguments()
    {
        var tables = L.Languages.Select(l => (l.Code, Table: L.Load(l.Code))).ToList();
        var bad = new List<string>();
        foreach (var file in CodeFiles().Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var text = StripComments(file, File.ReadAllText(file));
            foreach (Match m in FormatCall().Matches(text))
            {
                if (CountArguments(text, m.Index + m.Length) is not { } count) continue;
                // L.P 的第一个参数是数量（选单复数用），后面的才给文字。
                if (m.Groups[1].Value == "P") count--;
                var key = m.Groups[2].Value;
                foreach (var (code, table) in tables)
                    foreach (var form in new[] { key, key + ".one", key + ".other" })
                        if (table.TryGetValue(form, out var value) && MaxIndex(value) + 1 > count)
                            bad.Add($"{Path.GetFileName(file)}: {form} 传了 {count} 个参数，{code} 要 {MaxIndex(value) + 1} 个");
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Distinct().Take(30)));
    }

    /// <summary>从键后面开始数到调用的右括号，数出键后面顶层的参数个数；碰到展开（..）这类认不出的写法返回 null。</summary>
    internal static int? CountArguments(string text, int start)
    {
        int depth = 0, count = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                // 跳过字符串（普通、@ 逐字、$ 插值都按引号配对粗略处理）。
                var verbatim = text[i - 1] == '@' || i > 1 && text[i - 2] == '@';
                for (i++; i < text.Length; i++)
                {
                    if (!verbatim && text[i] == '\\') { i++; continue; }
                    if (text[i] == '"')
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                        break;
                    }
                }
                continue;
            }
            if (c == '\'')
            {
                for (i++; i < text.Length && text[i] != '\''; i++)
                    if (text[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return count;
                depth--;
            }
            else if (c == ',' && depth == 0) count++;
            else if (c == '.' && depth == 0 && i + 1 < text.Length && text[i + 1] == '.') return null;
        }
        return null;
    }

    /// <summary>同一个 JSON 文件里重复的键（后一个会悄悄盖掉前一个）。</summary>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void NoDuplicateKeys(string code)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Src, "Amber.Core", "Localization", code + ".json"));
        var reader = new System.Text.Json.Utf8JsonReader(bytes, new System.Text.Json.JsonReaderOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        while (reader.Read())
            if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1 && !seen.Add(reader.GetString()!))
                duplicates.Add(reader.GetString()!);
        Assert.Empty(duplicates);
    }

    /// <summary>每种语言都编进了程序集，而且不是空的（资源名写错时 Load 会悄悄返回空表）。</summary>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void ResourceExistsAndIsNotEmpty(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Amber.Core.Localization.{code}.json");
        Assert.NotNull(stream);
        Assert.True(stream.Length > 100);
        Assert.True(L.Load(code).Count > 100);
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-HK", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    public void CultureMapsToLanguage(string culture, string expected) =>
        Assert.Equal(expected, L.FromCulture(new System.Globalization.CultureInfo(culture)));

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("EN", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("ja", "ja")]
    [InlineData("system", null)]
    [InlineData("fr", null)]
    [InlineData("xx-NOT-A-CULTURE", null)]
    [InlineData("", null)]
    public void SavedCodesResolve(string code, string? expected) => Assert.Equal(expected, L.Resolve(code));

    /// <summary>每个 .one 都有对应的 .other 或原键；count 为 1 时取 .one，否则不取。</summary>
    [Fact]
    public void PluralFormsArePaired()
    {
        var source = L.Load(L.Source);
        foreach (var (code, _) in L.Languages)
        {
            var table = L.Load(code);
            foreach (var key in table.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)))
                Assert.True(table.ContainsKey(key[..^4] + ".other") || source.ContainsKey(key[..^4]), $"{code}：{key} 没有对应的复数写法");
        }
        var fake = new Dictionary<string, string> { ["x"] = "{0} mods", ["x.one"] = "1 mod" };
        Assert.Equal("x.one", L.PluralKey(fake, "x", 1));
        Assert.Equal("x", L.PluralKey(fake, "x", 2));
        Assert.Equal("y", L.PluralKey(fake, "y", 1));
    }

    [Fact]
    public void ArgumentCounterHandlesNesting()
    {
        const string call = "L.F(\"k\", a(b, c), $\"{x}, y\", new[] { 1, 2 }, 'x') + 1";
        Assert.Equal(4, CountArguments(call, call.IndexOf('"', 5) + 1));
    }

    [Fact]
    public void CompactCountsRoundIntoTheNextUnit()
    {
        // 999,960 四舍五入后是 1000K / 100.0 万这一类：要换成下一级单位或者保持在合理范围，不能出现 1000.0 这种写法。
        var text = Amber.App.ViewModels.ProjectItem.FormatCount(99_999_600).Replace(",", "");
        Assert.DoesNotContain("10000.0", text);
        Assert.DoesNotContain("1000.0", text);
    }

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
