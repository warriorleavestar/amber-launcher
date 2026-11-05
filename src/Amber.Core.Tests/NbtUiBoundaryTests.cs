using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>用真实命令和既有界面测试线程检查 NBT 拒绝后的状态与撤销。</summary>
public sealed class NbtUiBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-nbt-ui-" + Guid.NewGuid().ToString("N"));

    public NbtUiBoundaryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void RejectedDatapackToggleRestoresStateWithoutRetryingAndAllowsLaterEdits() => UiTestThread.Run(() =>
    {
        var path = Path.Combine(_root, "level.dat");
        var root = new Dictionary<string, object>
        {
            ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.MaxTags - 6).ToList()),
            ["Data"] = new Dictionary<string, object>
            {
                ["DataPacks"] = new Dictionary<string, object>
                {
                    ["Enabled"] = new Nbt.NbtList(8, []),
                    ["Disabled"] = new Nbt.NbtList(8, [])
                }
            }
        };
        Nbt.WriteFile(path, root, gzip: true);
        var original = File.ReadAllBytes(path);
        var reports = new List<string>();
        var item = new DatapackItem(new LocalDatapack("pack.zip", "", true, null, false), _root, message =>
        {
            reports.Add(message);
            // 回归时只执行到第二次报错即中止，不让错误回滚无限排队。
            if (reports.Count > 1) throw new InvalidOperationException("Repeated datapack edit");
        });
        item.Enabled = false;
        Assert.Null(Record.Exception(() => Dispatcher.UIThread.RunJobs()));
        Assert.Single(reports);
        Assert.Equal(L.F("instance.status.change_failed", L.T("core.game.nbt.invaliddata")), reports[0]);
        Assert.True(item.Enabled);
        Assert.Equal(original, File.ReadAllBytes(path));

        // 失败后可继续合法编辑；最近成功状态应随每一次切换更新。
        root.Remove("padding");
        Nbt.WriteFile(path, root, gzip: true);
        reports.Clear();
        item.Enabled = false;
        Assert.False(item.Enabled);
        Assert.Single(reports);
        Assert.Contains("file/pack.zip", DatapackList(path, "Disabled"));
        reports.Clear();
        item.Enabled = true;
        Assert.True(item.Enabled);
        Assert.Single(reports);
        Assert.Contains("file/pack.zip", DatapackList(path, "Enabled"));
    });

    [Fact]
    public void AddServerBudgetFailureReportsErrorAndPreservesFormAndList() => UiTestThread.Run(() =>
    {
        var path = Path.Combine(_root, "servers.dat");
        Nbt.WriteFile(path, new Dictionary<string, object>
        {
            ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.MaxTags - 7).ToList()),
            ["servers"] = new Nbt.NbtList(10, [new Dictionary<string, object>
            {
                ["name"] = "old", ["ip"] = "old.invalid", ["hidden"] = (byte)0
            }])
        });
        var original = File.ReadAllBytes(path);
        using var service = CreateService();
        var vm = CreateViewModel(service);
        var old = new ServerItem(new ServerEntry("old", "old.invalid"));
        vm.Servers.Add(old);
        vm.NewServerName = " new ";
        vm.NewServerAddress = " new.invalid ";

        Assert.Null(Record.Exception(() => vm.AddServerCommand.Execute(null)));
        Assert.Equal(StatusKind.Error, vm.StatusInfo?.Kind);
        Assert.Equal("instance.status.change_failed", vm.StatusInfo?.Key);
        Assert.Equal(" new ", vm.NewServerName);
        Assert.Equal(" new.invalid ", vm.NewServerAddress);
        Assert.Same(old, Assert.Single(vm.Servers));
        Assert.Equal(original, File.ReadAllBytes(path));
    });

    [Fact]
    public void RemoveServerBudgetFailurePreservesListAndLaterValidRemovalWorks() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        try
        {
            var path = Path.Combine(_root, "servers.dat");
            var root = new Dictionary<string, object> { ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.MaxTags).ToList()) };
            Nbt.WriteFile(path, root, false, Nbt.DefaultReadLimits with { Values = Nbt.MaxTags + 2 });
            var original = File.ReadAllBytes(path);
            using var service = CreateService();
            var vm = CreateViewModel(service);
            var item = new ServerItem(new ServerEntry("old", "old.invalid"));
            vm.Servers.Add(item);

            Assert.Null(Record.Exception(() => vm.RemoveServerCommand.Execute(item)));
            Assert.Equal(StatusKind.Error, vm.StatusInfo?.Kind);
            Assert.Equal("instance.status.delete_failed", vm.StatusInfo?.Key);
            Assert.Same(item, Assert.Single(vm.Servers));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(0, Toasts.PendingCount);

            Nbt.WriteFile(path, new Dictionary<string, object>());
            ServerList.Add(_root, item.Entry);
            vm.RemoveServerCommand.Execute(item);
            Assert.Empty(vm.Servers);
            Assert.Empty(ServerList.Read(_root));
            Assert.Equal(1, Toasts.PendingCount);
            Assert.True(Toasts.UndoLatest());
            Assert.Same(item, Assert.Single(vm.Servers));
            Assert.Single(ServerList.Read(_root));
            Assert.Equal(0, Toasts.PendingCount);
        }
        finally { Toasts.ClearHistory(); }
    });

    [Fact]
    public void RejectedUndoKeepsHistoryAndCanRestoreAfterTheBudgetIsAvailable() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        try
        {
            var path = Path.Combine(_root, "servers.dat");
            ServerList.Add(_root, new ServerEntry("old", "old.invalid"));
            using var service = CreateService();
            var vm = CreateViewModel(service);
            var item = new ServerItem(new ServerEntry("old", "old.invalid"));
            vm.Servers.Add(item);
            vm.RemoveServerCommand.Execute(item);
            Assert.Equal(1, Toasts.PendingCount);
            Assert.Empty(vm.Servers);
            Nbt.WriteFile(path, new Dictionary<string, object>
            {
                ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.MaxTags - 2).ToList())
            });
            var original = File.ReadAllBytes(path);

            Assert.Null(Record.Exception(() => Assert.True(Toasts.UndoLatest())));
            Assert.Equal(1, Toasts.PendingCount);
            Assert.Empty(vm.Servers);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(ServerList.Read(_root));

            Nbt.WriteFile(path, new Dictionary<string, object>());
            Assert.True(Toasts.UndoLatest());
            Assert.Equal(0, Toasts.PendingCount);
            Assert.Same(item, Assert.Single(vm.Servers));
            Assert.Single(ServerList.Read(_root));
            Assert.Equal("trash.restored", vm.StatusInfo?.Key);
        }
        finally { Toasts.ClearHistory(); }
    });

    private LauncherService CreateService() => new(new AppSettings
    {
        GameDirectory = _root,
        SavePathForTest = Path.Combine(_root, "settings.json"),
        PlayerName = "",
        AutoJavaDownload = false,
    });

    private InstanceViewModel CreateViewModel(LauncherService service) => new(service)
    {
        Current = new GameInstance("test", "1.21", null, null, _root)
    };

    private static List<object> DatapackList(string path, string key)
    {
        var data = (Dictionary<string, object>)Nbt.ReadFile(path)["Data"];
        var packs = (Dictionary<string, object>)data["DataPacks"];
        return ((Nbt.NbtList)packs[key]).Items;
    }
}
