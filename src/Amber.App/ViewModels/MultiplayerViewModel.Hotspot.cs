using Amber.Core.Multiplayer;

namespace Amber.App.ViewModels;

public sealed partial class MultiplayerViewModel
{
    public HotspotSharingViewModel Hotspot { get; private set; } = null!;

    private void InitHotspot() => Hotspot = new HotspotSharingViewModel(_exitSwitchLock);

    private void SyncHotspotSharing() => Hotspot.UpdateSession(
        _session is { HasExited: false } ? _session as IHotspotSharingSession : null,
        _session?.Options.Tun == true,
        _session?.Options.ExitNode,
        _sessionCts?.Token ?? default);
}
