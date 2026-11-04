using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>联机和聊天的数据流怎么封装。房主选，加入方跟着房间码里的设置走（两边必须一样）。</summary>
public enum TransportMode
{
    /// <summary>通用：不额外封装，只靠 EasyTier 自带的加密。开销最小，什么 TCP 都能转。</summary>
    General = 0,
    /// <summary>MC 专用：为 Minecraft 的小包、低延迟调优（关 Nagle、合并写入），加一层轻量的认证加密。</summary>
    Minecraft = 1,
    /// <summary>高加密：X25519 前向保密握手 + ChaCha20-Poly1305，定期换钥，长度按 64 字节对齐。</summary>
    High = 2,
    /// <summary>极端加密：再加 ML-KEM-768 混合（抗量子），更频繁换钥，长度填充到固定档位，空闲时发掩护流量。</summary>
    Extreme = 3,
}

/// <summary>每种模式的具体参数。</summary>
public sealed record TransportProfile(
    TransportMode Mode,
    NoisePattern Pattern,
    /// <summary>发这么多条记录后换钥。</summary>
    long RekeyEveryRecords,
    /// <summary>距上次换钥这么久后，下一条记录换钥。</summary>
    TimeSpan RekeyEvery,
    /// <summary>明文长度对齐到这个倍数（1 = 不填充）。</summary>
    int PadTo,
    /// <summary>填充到这些固定档位之一（优先于 PadTo）。</summary>
    IReadOnlyList<int>? PadBuckets,
    /// <summary>空闲多久发一条掩护记录（null = 不发）。</summary>
    TimeSpan? CoverTrafficIdle)
{
    public static TransportProfile For(TransportMode mode) => mode switch
    {
        TransportMode.Minecraft => new(mode, NoisePattern.NNpsk0, 1L << 24, TimeSpan.FromHours(1), 1, null, null),
        TransportMode.High => new(mode, NoisePattern.NNpsk0, 1L << 20, TimeSpan.FromMinutes(10), 64, null, null),
        TransportMode.Extreme => new(mode, NoisePattern.NNhfspsk0, 1L << 14, TimeSpan.FromMinutes(1), 1,
            [256, 1024, 4096, 16384, SecureStream.MaxPlaintext], TimeSpan.FromMilliseconds(250)),
        _ => throw new ArgumentException(L.T("core.mp.transport.general_unwrapped")),
    };

    public static string Describe(TransportMode mode) => mode switch
    {
        TransportMode.General => L.T("core.mp.transport.description.general"),
        TransportMode.Minecraft => L.T("core.mp.transport.description.minecraft"),
        TransportMode.High => L.T("core.mp.transport.description.high"),
        TransportMode.Extreme => L.T("core.mp.transport.description.extreme"),
        _ => "",
    };
}
