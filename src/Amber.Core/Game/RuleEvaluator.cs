using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Amber.Core.Game;

/// <summary>按当前系统求值版本 JSON 里的 rules。</summary>
public sealed class RuleEvaluator
{
    /// <summary>Mojang 的系统名：windows / linux / osx。</summary>
    public string OsName { get; init; } = CurrentOsName();

    /// <summary>Mojang 的架构名：x86 / x86_64 / arm64 等。</summary>
    public string Arch { get; init; } = CurrentArch();

    public string OsVersion { get; init; } = Environment.OSVersion.Version.ToString();

    /// <summary>已开启的特性（如 has_custom_resolution）。未列出的视为 false。</summary>
    public HashSet<string> Features { get; init; } = [];

    public static RuleEvaluator Current { get; } = new();

    /// <summary>没有规则视为允许；否则最后一条匹配的规则决定结果，没有匹配则不允许。</summary>
    public bool IsAllowed(IReadOnlyList<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule)) allowed = rule.Action == "allow";
        }
        return allowed;
    }

    private bool Matches(Rule rule)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && os.Name != OsName) return false;
            if (os.Arch is not null && os.Arch != Arch) return false;
            if (os.Version is not null && !Regex.IsMatch(OsVersion, os.Version)) return false;
        }
        if (rule.Features is not null)
        {
            foreach (var (name, expected) in rule.Features)
            {
                if (Features.Contains(name) != expected) return false;
            }
        }
        return true;
    }

    /// <summary>旧 natives 字段里 ${arch} 的取值：32 或 64。</summary>
    public string ArchBits => Arch is "x86" ? "32" : "64";

    public static string CurrentOsName() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string CurrentArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        var a => a.ToString().ToLowerInvariant(),
    };
}
