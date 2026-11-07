using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>联机页引导（issue #23）：状态行随输入更新，贴错输入框的码转到对的地方。</summary>
public sealed class MultiplayerGuideTests
{
    private static (LauncherService Service, MultiplayerViewModel Vm, List<string?> Changed) Create()
    {
        var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return (service, vm, changed);
    }

    [Fact]
    public void GameStatusFollowsManualPort() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        Assert.False(vm.HasHostGame);
        Assert.Equal(L.T("multiplayer.guide.game_missing"), vm.GameStatusText);

        vm.ManualPort = "25565";
        Assert.True(vm.HasHostGame);
        Assert.Equal(L.F("multiplayer.guide.port_ready", 25565), vm.GameStatusText);
        Assert.Contains(nameof(MultiplayerViewModel.GameStatusText), changed);
        Assert.Contains(nameof(MultiplayerViewModel.HasHostGame), changed);

        vm.ManualPort = "99999";
        Assert.False(vm.HasHostGame);
    });

    [Fact]
    public void NodeStatusFollowsNodeList() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        Assert.False(vm.HasNodes);
        Assert.Equal(L.T("multiplayer.nodes.header_empty"), vm.NodesHeaderText);

        vm.Nodes.Add(new NodeItem("tcp://example.com:11010"));
        Assert.True(vm.HasNodes);
        Assert.Equal(L.F("multiplayer.nodes.header_count", 1), vm.NodesHeaderText);
        Assert.Equal(L.F("multiplayer.guide.nodes_ready", 1), vm.NodesStatusText);
        Assert.Contains(nameof(MultiplayerViewModel.NodesStatusText), changed);
        Assert.Contains(nameof(MultiplayerViewModel.NodesHeaderText), changed);
    });

    [Fact]
    public void AdvancedHeaderListsWhatIsOn() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var _ = service;
        Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);

        vm.FullLanMode = true;
        Assert.Contains(nameof(MultiplayerViewModel.AdvancedHeaderText), changed);
        if (vm.CanUseFullLan)
            Assert.Contains(L.T("multiplayer.advanced.item.full_lan"), vm.AdvancedHeaderText);
        else
        {
            Assert.False(vm.FullLanMode);
            Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);
        }

        changed.Clear();
        vm.AllowGameProxy = true;
        Assert.Contains(nameof(MultiplayerViewModel.AdvancedHeaderText), changed);
        Assert.Contains(L.T("multiplayer.advanced.item.proxy"), vm.AdvancedHeaderText);

        vm.FullLanMode = false;
        vm.AllowGameProxy = false;
        Assert.Equal(L.T("multiplayer.advanced.title"), vm.AdvancedHeaderText);
    });

    [Fact]
    public void NodeShareCodeInRoomBoxMovesToImportBoxWithoutImporting() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        var code = RelayNodes.Encode(null, ["tcp://example.com:11010"]);
        vm.JoinCode = code;
        vm.JoinCommand.Execute(null);
        Assert.Equal("", vm.JoinCode);
        Assert.Equal(code, vm.ShareCodeText);
        Assert.Empty(vm.Nodes);
        Assert.True(vm.NodesExpanded);
        Assert.Equal(L.T("multiplayer.code.node_misplaced"), vm.Status);
        Assert.True(vm.IsIdle);
    });

    [Fact]
    public void NodeShareCodeInDirectBoxMovesToImportBoxWithoutImporting() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.DirectInviteInput = "节点：" + RelayNodes.Encode(null, ["tcp://example.org:11010"]);
        vm.JoinDirectCommand.Execute(null);
        Assert.Equal("", vm.DirectInviteInput);
        Assert.Empty(vm.Nodes);
        // 用户确认后自己点导入，才真正加进去。
        vm.ImportNodesCommand.Execute(null);
        Assert.Equal("tcp://example.org:11010", Assert.Single(vm.Nodes).Url);
    });

    [Fact]
    public void ReplyCodeInInviteBoxExplainsWhereItGoes() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.DirectInviteInput = "JAY2R-eyJ2IjoxfQ";
        vm.JoinDirectCommand.Execute(null);
        Assert.Equal("JAY2R-eyJ2IjoxfQ", vm.DirectInviteInput);
        Assert.Equal(L.T("multiplayer.code.reply_misplaced"), vm.Status);
        Assert.True(vm.IsIdle);
    });

    /// <summary>旧版本的直连码（里面是完整的房间码）：不接受，提示对方更新。</summary>
    [Theory]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ")]
    [InlineData("AMBREPLY1-eyJhIjoxfQ")]
    public void OldDirectCodeShowsTheUpdateHint(string code) => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.DirectInviteInput = code;
        vm.JoinDirectCommand.Execute(null);
        Assert.Equal(L.T("multiplayer.direct.old_code"), vm.Status);
        Assert.True(vm.IsIdle);
        vm.JoinCode = code;
        vm.JoinCommand.Execute(null);
        Assert.Equal(L.T("multiplayer.direct.old_code"), vm.Status);
        Assert.True(vm.IsIdle);
    });

    [Fact]
    public void DirectInviteInRoomBoxMovesToDirectBox() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        const string code = "JAY2D-eyJ2IjoxfQ";
        vm.JoinCode = code;
        vm.JoinCommand.Execute(null);
        Assert.Equal("", vm.JoinCode);
        Assert.Equal(code, vm.DirectInviteInput);
        Assert.True(vm.IsIdle);
    });

    [Fact]
    public void InviteInDirectBoxMovesToRoomBox() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        const string code = "JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2";
        vm.DirectInviteInput = code;
        vm.JoinDirectCommand.Execute(null);
        Assert.Equal("", vm.DirectInviteInput);
        Assert.Equal(code, vm.JoinCode);
        Assert.True(vm.IsIdle);
    });

    /// <summary>旧版本的 AMB- 房间码贴进直连框：不再转去加入，提示请房主更新。</summary>
    [Fact]
    public void OldRoomCodeInDirectBoxShowsUpdateHint() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.DirectInviteInput = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565";
        vm.JoinDirectCommand.Execute(null);
        Assert.Equal("", vm.JoinCode);
        Assert.Contains("JAY2-", vm.Status);
        Assert.True(vm.IsIdle);
    });

    [Fact]
    public void RoomCodeInRoomBoxIsNotRerouted() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var __ = service;
        vm.JoinCode = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565";
        vm.JoinCommand.Execute(null);
        // 组件没下载：照常停在原来的流程上，码留在输入框里。
        Assert.Equal("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565", vm.JoinCode);
        Assert.Equal("", vm.DirectInviteInput);
    });
}
