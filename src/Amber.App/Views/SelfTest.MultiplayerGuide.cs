using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>联机引导的窗口自检：不下载组件、不建立连接，检查真实控件的绑定、操作入口和小窗口布局。</summary>
    private async Task MultiplayerGuideAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        main.CurrentPage = vm;
        var page = await FindAsync<MultiplayerPage>(_ => true);
        if (page is null)
        {
            Check("联机引导：页面显示", false);
            return;
        }
        await Task.Delay(300);
        // 本范围只检查引导；即使测试目录已有组件，也不建立任何连接。
        vm.IsComponentReady = false;
        bool HasText(string key) => page.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.IsEffectivelyVisible && t.Text == L.T(key));

        Check("联机引导：两种方式与前置条件都有说明",
            HasText("multiplayer.guide.hint") && HasText("multiplayer.room_mode.heading")
            && HasText("multiplayer.direct.heading"));
        Check("联机引导：共用游戏配置独立显示", HasText("multiplayer.game.title") && HasText("multiplayer.game.hint"));

        var expanders = page.GetVisualDescendants().OfType<Expander>().ToArray();
        var nodes = expanders.Single(e => (e.Header as TextBlock)?.Text == vm.NodesHeaderText);
        var advanced = expanders.Single(e => (e.Header as TextBlock)?.Text == vm.AdvancedHeaderText);
        Check("联机引导：无节点时显示缺少条件并展开节点配置", !vm.HasNodes && nodes.IsExpanded
            && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == vm.NodesStatusText));
        Check("联机引导：高级选项默认收起", !advanced.IsExpanded);
        nodes.IsExpanded = false;
        Check("联机引导：手动折叠节点区同步到状态", !vm.NodesExpanded);
        nodes.IsExpanded = true;
        await ShotAsync("联机引导默认状态");
        await LayoutHereAsync("联机引导默认状态");

        var port = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.lan.port_placeholder"));
        Dispatcher.UIThread.Invoke(() => port!.Text = "25565");
        await Task.Delay(200);
        Check("联机引导：输入端口后两个开房入口同步显示游戏状态", vm.HasHostGame
            && page.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == L.F("multiplayer.guide.port_ready", 25565)) == 2);

        var roomCode = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.join.code_placeholder"));
        var nodeCode = RelayNodes.Encode(null, ["tcp://example.invalid:11010"]);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = nodeCode);
        Check("联机引导：点击加入把节点码转至导入框", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand))));
        await Task.Delay(200);
        var importBox = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.nodes.share_placeholder"));
        Check("联机引导：转移节点码不静默导入", vm.Nodes.Count == 0 && nodes.IsExpanded
            && roomCode!.Text == "" && importBox?.Text == nodeCode && vm.IsIdle);
        Check("联机引导：用户点击导入才添加节点", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.ImportNodesCommand)))
            && vm.Nodes.Count == 1 && vm.Nodes[0].Url == "tcp://example.invalid:11010");
        var importedNode = vm.Nodes.Single();
        Check("联机引导：节点数量和状态行同步更新", (nodes.Header as TextBlock)?.Text == L.F("multiplayer.nodes.header_count", 1)
            && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == L.F("multiplayer.guide.nodes_ready", 1)));
        // 撤掉测试节点，后续再次运行仍从空节点状态开始。
        vm.RemoveNodeCommand.Execute(importedNode);

        var directCode = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.direct.invite_placeholder"));
        const string directInvite = "AMBDIRECT1-eyJhIjoxfQ";
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = directInvite);
        Check("联机引导：房间框的直连邀请码转到直连框", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand)))
            && roomCode!.Text == "" && directCode!.Text == directInvite && vm.IsIdle);
        const string relayRoom = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565";
        Dispatcher.UIThread.Invoke(() => directCode!.Text = relayRoom);
        Check("联机引导：直连框的房间码转到房间框", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinDirectCommand)))
            && directCode!.Text == "" && roomCode!.Text == relayRoom && vm.IsIdle);
        Dispatcher.UIThread.Invoke(() => directCode!.Text = "AMBREPLY1-eyJhIjoxfQ");
        Check("联机引导：点击加入识别回复码", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinDirectCommand))));
        await Task.Delay(200);
        Check("联机引导：回复码误贴时提示交给房主", vm.Status == L.T("multiplayer.code.reply_misplaced") && vm.IsIdle);

        Dispatcher.UIThread.Invoke(() =>
        {
            importBox!.Text = "";
            directCode!.Text = "";
            roomCode!.Text = "";
            advanced.IsExpanded = true;
        });
        await Task.Delay(200);
        Check("联机引导：展开后能看到高级选项和用途说明", HasText("multiplayer.lan.full_mode_hint") && HasText("multiplayer.transport.hint"));
        advanced.BringIntoView();
        await ShotAsync("联机引导高级选项");
        await LayoutHereAsync("联机引导高级选项");

        Dispatcher.UIThread.Invoke(() =>
        {
            advanced.IsExpanded = false;
            nodes.IsExpanded = false;
            vm.SetStatus(null);
            page.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, 0);
        });
        window.Width = 1280;
        window.Height = 800;
        await ShotAsync("联机引导正常窗口");
        var scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
        void ScrollToHeading(string key)
        {
            var heading = page.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == L.T(key));
            if (heading.TranslatePoint(new Point(), scroll) is { } position)
                scroll.Offset = new Vector(0, scroll.Offset.Y + position.Y);
        }
        ScrollToHeading("multiplayer.room_mode.heading");
        await ShotAsync("联机引导正常窗口房间入口");
        window.Width = 760;
        window.Height = 440;
        scroll.Offset = new Vector(0, 0);
        await ShotAsync("联机引导最小窗口");
        ScrollToHeading("multiplayer.room_mode.heading");
        await ShotAsync("联机引导最小窗口房间入口");
        ScrollToHeading("multiplayer.direct.heading");
        await ShotAsync("联机引导最小窗口直连入口");
    }
}
