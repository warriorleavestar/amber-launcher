namespace Amber.Core.Auth;

/// <summary>离线玩家名哪里不行（硬性规则，不满足不能添加）。</summary>
public enum OfflineNameProblem
{
    None,
    Empty,
    TooShort,
    TooLong,
    HasSpace,
}

/// <summary>检查结果：Problem 不是 None 时不能添加；NotAscii 只是提醒（能用，但部分服务器会拒绝）。</summary>
public readonly record struct OfflineNameCheck(OfflineNameProblem Problem, bool NotAscii)
{
    public bool Ok => Problem == OfflineNameProblem.None;
}

/// <summary>
/// 离线玩家名的规则（改版规格 3.1）：3–16 个字符、不能为空、不能有空格是硬性规则；
/// 用了英文字母、数字、下划线以外的字符只提醒，仍然允许。
/// </summary>
public static class OfflineNameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    public static OfflineNameCheck Check(string? name)
    {
        name ??= "";
        var notAscii = name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && !char.IsWhiteSpace(c));
        // 按看到的字符数算（中文、emoji 都算一个），不按 UTF-16 单元。
        var length = new System.Globalization.StringInfo(name).LengthInTextElements;
        var problem = name.Length == 0 ? OfflineNameProblem.Empty
            : name.Any(char.IsWhiteSpace) ? OfflineNameProblem.HasSpace
            : length < MinLength ? OfflineNameProblem.TooShort
            : length > MaxLength ? OfflineNameProblem.TooLong
            : OfflineNameProblem.None;
        return new OfflineNameCheck(problem, notAscii);
    }
}
