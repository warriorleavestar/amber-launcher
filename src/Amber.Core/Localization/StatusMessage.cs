namespace Amber.Core.Localization;

public enum StatusKind { Info, Success, Warning, Error }

/// <summary>
/// 一条状态消息：存翻译键和参数，显示时才按当前语言格式化（换语言后重新显示就是新语言）。
/// “注意：……”这类附加警告单独发一条 Warning，不拼在成功消息后面。
/// </summary>
/// <param name="Detail">详情（比如完整的异常信息），界面上放在“详情”里，可以复制。</param>
public sealed record StatusMessage(string Key, object?[] Args, StatusKind Kind = StatusKind.Info, string? Detail = null)
{
    /// <summary>按当前语言格式化好的文字。</summary>
    public string Text => Format();

    /// <summary>数量会影响写法时（英文单复数）：count 用来选 key.one / key.other。</summary>
    public long? Count { get; init; }

    public string Format() => Count is { } n ? L.P(Key, n, Args) : L.F(Key, Args);

    public override string ToString() => Format();

    /// <summary>
    /// 按引用比较：两条键和参数都一样的消息也是两条（关掉一条错误后同样的错误再出现，界面要能再显示）。
    /// 记录类型默认按值比较，没参数时 params 传的是同一个空数组，会被当成“没变”。
    /// </summary>
    public bool Equals(StatusMessage? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public static StatusMessage Info(string key, params object?[] args) => new(key, args);
    public static StatusMessage Success(string key, params object?[] args) => new(key, args, StatusKind.Success);
    public static StatusMessage Warning(string key, params object?[] args) => new(key, args, StatusKind.Warning);
    public static StatusMessage Error(string key, params object?[] args) => new(key, args, StatusKind.Error);

    /// <summary>出错：说明用翻译键，异常原文放进详情。</summary>
    public static StatusMessage Failure(string key, Exception e, params object?[] args) => new(key, args, StatusKind.Error, e.ToString());

    /// <summary>不用翻译的现成文字（比如外部程序的输出）。</summary>
    public static StatusMessage Plain(string text, StatusKind kind = StatusKind.Info) => new("common.raw", [text], kind);
}
