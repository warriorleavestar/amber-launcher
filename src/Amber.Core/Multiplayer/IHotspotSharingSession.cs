namespace Amber.Core.Multiplayer;

/// <summary>已经建立的出口会话可把其虚拟连接共享给本机 Windows 移动热点。</summary>
public interface IHotspotSharingSession
{
    /// <summary>仅在提权守护确认共享配置后为真；不代表已完成远端互联网连通性测试。</summary>
    bool HotspotSharingActive { get; }

    /// <summary>启停共享。由会话所有者串行处理，重启、停止会话或命令管道断开时撤销。</summary>
    Task SetHotspotSharingAsync(bool enabled, CancellationToken ct = default);
}
