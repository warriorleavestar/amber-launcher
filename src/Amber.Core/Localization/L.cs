using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Amber.Core.Localization;

/// <summary>
/// 界面文字的翻译。每种语言一个 JSON 文件（键 → 文字），作为嵌入资源编进程序集：
///   Localization/zh-Hans.json（原文，所有键都在这里）、zh-Hant.json、en.json、ja.json。
/// 找不到的键先退回简体中文，再退回键本身（这样漏翻的地方一眼能看出来）。
/// 用法：C# 里 L.T("home.launch")、L.F("home.running", id, memory)（{0}{1} 占位）、L.P("…", n, …)（区分单复数）；
/// XAML 里 {l:T home.launch}。换语言时触发 Changed，界面上用 {l:T} 绑定的文字立刻跟着换。
/// </summary>
public static class L
{
    public const string Source = "zh-Hans";

    /// <summary>支持的语言（代码, 用这种语言写的名字）。</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("en", "English"),
        ("ja", "日本語"),
    ];

    /// <summary>每种语言格式化数字、日期用的区域（不跟 Windows 的区域设置走，界面是哪种语言就按哪种写）。</summary>
    private static readonly Dictionary<string, CultureInfo> Cultures = new()
    {
        ["zh-Hans"] = CultureInfo.GetCultureInfo("zh-CN"),
        ["zh-Hant"] = CultureInfo.GetCultureInfo("zh-TW"),
        ["en"] = CultureInfo.GetCultureInfo("en-US"),
        ["ja"] = CultureInfo.GetCultureInfo("ja-JP"),
    };

    /// <summary>当前语言的全部状态放在一个不可变对象里，换语言时整体替换，别的线程读到的总是一致的一份。</summary>
    private sealed record State(string Language, IReadOnlyDictionary<string, string> Table, CultureInfo Culture);

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static readonly IReadOnlyDictionary<string, string> Fallback = Load(Source);
    private static volatile State _state = new(Source, Fallback, Cultures[Source]);

    public static string Language => _state.Language;

    /// <summary>当前语言格式化数字用的区域。</summary>
    public static CultureInfo Culture => _state.Culture;

    /// <summary>换了语言。可能在任何线程上触发，界面那边自己切到界面线程。</summary>
    public static event Action? Changed;

    /// <summary>按系统语言选：沿区域的上级链找支持的语言（zh-TW、zh-HK、zh-MO → 繁体；zh-CN、zh-SG、zh-Hans-HK → 简体）；都不是时用英语。</summary>
    public static string FromCulture(CultureInfo culture) => Match(culture) ?? "en";

    /// <summary>沿上级链找支持的语言，找不到返回 null。</summary>
    private static string? Match(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            foreach (var (code, _) in Languages)
                if (string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase)) return code;
            // 旧写法：zh-CHT / zh-CHS。
            if (string.Equals(c.Name, "zh-CHT", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
            if (string.Equals(c.Name, "zh-CHS", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, "zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            if (ReferenceEquals(c, c.Parent)) break;
        }
        return null;
    }

    /// <summary>
    /// 设置里存的语言代码换成支持的语言：大小写、地区写法（en-US、zh-CN）都认；null、"system"、认不出的代码返回 null（跟随系统）。
    /// </summary>
    public static string? Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("system", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return Match(CultureInfo.GetCultureInfo(code.Trim()));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>设置语言；null、"system" 或认不出的代码表示跟随系统。</summary>
    public static void SetLanguage(string? code)
    {
        var resolved = Resolve(code) ?? FromCulture(CultureInfo.CurrentUICulture);
        _state = new State(resolved, Load(resolved), Cultures[resolved]);
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        var table = _state.Table;
        return table.TryGetValue(key, out var text) ? text : Fallback.TryGetValue(key, out var source) ? source : key;
    }

    /// <summary>按当前语言的区域格式化。译文的占位符写错了（格式异常）时先用简体原文，再不行就直接显示文字，不让界面崩掉。</summary>
    public static string F(string key, params object?[] args)
    {
        var state = _state;
        try
        {
            return string.Format(state.Culture, T(key), args);
        }
        catch (FormatException)
        {
            try
            {
                return Fallback.TryGetValue(key, out var source) ? string.Format(state.Culture, source, args) : key;
            }
            catch (FormatException)
            {
                return T(key);
            }
        }
    }

    /// <summary>
    /// 区分单复数：count 是 1 时优先用 key.one，否则用 key.other，都没有就用 key（中文、日文只有一种写法）。
    /// args 照常传给 F（count 本身要显示的话也放在 args 里）。
    /// </summary>
    public static string P(string key, long count, params object?[] args) => F(PluralKey(_state.Table, key, count), args);

    /// <summary>在这张表里，count 对应用哪个键。</summary>
    internal static string PluralKey(IReadOnlyDictionary<string, string> table, string key, long count) =>
        count == 1 && table.ContainsKey(key + ".one") ? key + ".one"
        : table.ContainsKey(key + ".other") ? key + ".other"
        : key;

    /// <summary>某种语言的全部文字（检查漏翻用）。文件坏了（JSON 格式不对）时返回空表，界面退回简体原文，不会打不开。</summary>
    public static IReadOnlyDictionary<string, string> Load(string code) => Tables.GetOrAdd(code, static c =>
    {
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Amber.Core.Localization.{c}.json");
            if (stream is null) return new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) table[p.Name] = p.Value.GetString()!;
            return table;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new Dictionary<string, string>();
        }
    });
}
