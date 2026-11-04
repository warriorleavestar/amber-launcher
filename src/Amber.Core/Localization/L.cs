using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Amber.Core.Localization;

/// <summary>
/// 界面文字的翻译。每种语言一个 JSON 文件（键 → 文字），作为嵌入资源编进程序集：
///   Localization/zh-Hans.json（原文，所有键都在这里）、zh-Hant.json、en.json、ja.json。
/// 找不到的键先退回简体中文，再退回键本身（这样漏翻的地方一眼能看出来）。
/// 用法：C# 里 L.T("home.launch")、L.F("home.running", id, memory)（{0}{1} 占位）；XAML 里 {l:T home.launch}。
/// 换语言时触发 Changed，界面上用 {l:T} 绑定的文字立刻跟着换。
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

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static IReadOnlyDictionary<string, string> _current = Load(Source);
    private static readonly IReadOnlyDictionary<string, string> Fallback = _current;

    public static string Language { get; private set; } = Source;

    public static event Action? Changed;

    /// <summary>按系统语言选：繁体（台湾、香港、澳门或 Hant）、简体中文、日语、其他都用英语。</summary>
    public static string FromCulture(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "zh-Hans";
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        return "en";
    }

    /// <summary>设置语言；null 或 "system" 表示跟随系统。</summary>
    public static void SetLanguage(string? code)
    {
        var resolved = code is null or "" or "system" ? FromCulture(CultureInfo.CurrentUICulture) : code;
        if (Languages.All(l => l.Code != resolved)) resolved = Source;
        _current = Load(resolved);
        Language = resolved;
        Changed?.Invoke();
    }

    public static string T(string key) =>
        _current.TryGetValue(key, out var text) ? text : Fallback.TryGetValue(key, out var source) ? source : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>某种语言的全部文字（检查漏翻用）。</summary>
    public static IReadOnlyDictionary<string, string> Load(string code) => Tables.GetOrAdd(code, static c =>
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Amber.Core.Localization.{c}.json");
        if (stream is null) return new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) table[p.Name] = p.Value.GetString()!;
        return table;
    });
}
