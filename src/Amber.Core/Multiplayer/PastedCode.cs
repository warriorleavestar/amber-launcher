namespace Amber.Core.Multiplayer;

/// <summary>联机页上粘贴的码是哪一种。</summary>
public enum PastedCodeKind
{
    Unknown,
    /// <summary>旧版本的房间码 AMB-…：不能再用来加入，提示对方更新。</summary>
    Room,
    /// <summary>邀请码 JAY2-…：经联机节点的房间（先敲门、房主批准）。</summary>
    Invite,
    /// <summary>直连邀请码 JAY2D-…：房主发给朋友。</summary>
    DirectInvite,
    /// <summary>直连回复码 JAY2R-…：朋友发回房主。</summary>
    DirectReply,
    /// <summary>旧版本的直连码 AMBDIRECT1- / AMBREPLY1-（里面是完整的房间码）：不再接受，提示对方更新。</summary>
    OldDirect,
    /// <summary>节点分享码 AMBNODE-…。</summary>
    NodeShare,
    /// <summary>Jay 服务器邀请码 JAYS1-…：贴到“Jay 服务器”卡片。</summary>
    JayServer,
}

/// <summary>
/// 只按前缀认出码的种类，好把贴错输入框的码转到对的地方，或者告诉用户该贴在哪。
/// 不校验内容，真正解析仍由各自的 TryParse 负责。容忍大小写和前后多余的文字。
/// </summary>
public static class PastedCode
{
    public static PastedCodeKind Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PastedCodeKind.Unknown;
        var upper = text.ToUpperInvariant();
        // 先认长前缀：base64url 正文里可能碰巧出现 “AMB-”。
        var first = PastedCodeKind.Unknown;
        var firstAt = int.MaxValue;
        foreach (var (prefix, kind) in Prefixes)
        {
            var at = upper.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0 && at < firstAt)
            {
                first = kind;
                firstAt = at;
            }
        }
        if (first != PastedCodeKind.Unknown) return first;
        return RoomCode.TryParse(text, out _) ? PastedCodeKind.Room : PastedCodeKind.Unknown;
    }

    private static readonly (string Prefix, PastedCodeKind Kind)[] Prefixes =
    [
        ("JAY2D-", PastedCodeKind.DirectInvite),
        ("JAY2R-", PastedCodeKind.DirectReply),
        ("AMBDIRECT", PastedCodeKind.OldDirect),
        ("AMBREPLY", PastedCodeKind.OldDirect),
        ("AMBNODE-", PastedCodeKind.NodeShare),
        ("JAY2-", PastedCodeKind.Invite),
        ("JAYS1-", PastedCodeKind.JayServer),
    ];
}
