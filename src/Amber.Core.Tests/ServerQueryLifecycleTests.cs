using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>真实界面命令只查询回环服务器；旧批次离开后不能继续占用全局查询预算。</summary>
[Collection("服务器状态并发预算")]
public sealed class ServerQueryLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-server-ui-" + Guid.NewGuid().ToString("N"));

    public ServerQueryLifecycleTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("refresh")]
    [InlineData("instance")]
    [InlineData("page")]
    [InlineData("tab")]
    public async Task ReplacedOrHiddenBatchReleasesSlotsWithoutUpdatingOldItems(string change)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var oldListener = new TcpListener(IPAddress.Loopback, 0);
        using var currentListener = new TcpListener(IPAddress.Loopback, 0);
        oldListener.Start();
        currentListener.Start();
        var accepted = new List<TcpClient>();
        LauncherService? service = null;
        InstanceViewModel? vm = null;
        ServerItem[] oldItems = [];
        var oldUpdates = 0;
        try
        {
            WriteServers(_root, Address(oldListener), 20);
            UiTestThread.Run(() =>
            {
                service = new LauncherService(new AppSettings
                {
                    GameDirectory = _root,
                    SavePathForTest = Path.Combine(_root, "settings.json"),
                    PlayerName = "",
                    AutoJavaDownload = false,
                });
                vm = new InstanceViewModel(service)
                {
                    Current = new GameInstance("old", "1.21", null, null, _root),
                    IsCurrent = true,
                    TabIndex = (int)InstanceTab.Servers,
                };
                oldItems = vm.Servers.ToArray();
                Assert.Equal(20, oldItems.Length);
                foreach (var item in oldItems)
                    item.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(ServerItem.StatusText)) Interlocked.Increment(ref oldUpdates);
                    };
            });
            // 四个连接保持沉默，其余十六项只能排队，不能靠网络超时释放当前查询。
            for (var i = 0; i < 4; i++) accepted.Add(await oldListener.AcceptTcpClientAsync(stop.Token));
            Assert.False(oldListener.Pending());
            var currentDir = change == "instance" ? Path.Combine(_root, "other") : _root;
            WriteServers(currentDir, Address(currentListener), 1);
            UiTestThread.Run(() =>
            {
                switch (change)
                {
                    case "refresh": vm!.RefreshServersCommand.Execute(null); break;
                    case "instance": vm!.Current = new GameInstance("new", "1.21", null, null, currentDir); break;
                    case "page": vm!.IsCurrent = false; break;
                    case "tab": vm!.TabIndex = (int)InstanceTab.Mods; break;
                }
            });

            // 在重入页面/刷新新实例之前确认取消，避免下一次 LoadServers 掩盖缺失的生命周期清理。
            await PumpUntil(Task.WhenAll(accepted.Select(client => WaitForClose(client, stop.Token))), stop.Token);
            Assert.False(oldListener.Pending());
            UiTestThread.Run(() =>
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, Volatile.Read(ref oldUpdates));
                switch (change)
                {
                    case "instance": vm!.RefreshServersCommand.Execute(null); break;
                    case "page": vm!.IsCurrent = true; break;
                    case "tab": vm!.TabIndex = (int)InstanceTab.Servers; break;
                }
                Assert.Single(vm!.Servers);
                Assert.DoesNotContain(vm.Servers[0], oldItems);
            });

            var accepting = currentListener.AcceptTcpClientAsync(stop.Token).AsTask();
            await PumpUntil(accepting, stop.Token);
            using var current = await accepting;
            await PumpUntil(Reply(current, stop.Token), stop.Token);
            var updated = false;
            using var delivered = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            delivered.CancelAfter(TimeSpan.FromSeconds(3));
            while (!updated)
            {
                UiTestThread.Run(() =>
                {
                    Dispatcher.UIThread.RunJobs();
                    updated = vm!.Servers[0].StatusText.Contains("current-batch", StringComparison.Ordinal);
                    Assert.Equal(0, Volatile.Read(ref oldUpdates));
                });
                if (!updated) await Task.Delay(10, delivered.Token);
            }
            Assert.False(oldListener.Pending());
        }
        finally
        {
            UiTestThread.Run(() => { if (vm is not null) vm.IsCurrent = false; });
            stop.Cancel();
            oldListener.Stop();
            currentListener.Stop();
            foreach (var client in accepted) client.Dispose();
            service?.Dispose();
        }
    }

    private static string Address(TcpListener listener) => $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    private static void WriteServers(string dir, string address, int count) => Nbt.WriteFile(Path.Combine(dir, "servers.dat"), new Dictionary<string, object>
    {
        ["servers"] = new Nbt.NbtList(10, Enumerable.Range(0, count).Select(i => (object)new Dictionary<string, object>
        {
            ["name"] = "server-" + i,
            ["ip"] = address,
        }).ToList()),
    });

    private static async Task WaitForClose(TcpClient client, CancellationToken ct)
    {
        var bytes = new byte[1024];
        while (await client.GetStream().ReadAsync(bytes, ct) != 0) { }
    }

    private static async Task PumpUntil(Task task, CancellationToken ct)
    {
        // 复用的 UI 测试线程不运行主循环；网络 await 的界面续体仍须有机会退出和更新。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (!task.IsCompleted)
        {
            UiTestThread.Run(() => Dispatcher.UIThread.RunJobs());
            await Task.Delay(10, deadline.Token);
        }
        await task;
    }

    private static async Task Reply(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        await ReadPacket(stream, ct);
        await ReadPacket(stream, ct);
        var json = Encoding.UTF8.GetBytes("""{"version":{"name":"current-batch"},"players":{"online":1,"max":20}}""");
        Assert.InRange(json.Length + 2, 1, 127);
        await stream.WriteAsync(new byte[] { (byte)(json.Length + 2), 0, (byte)json.Length }.Concat(json).ToArray(), ct);
        var ping = await ReadPacket(stream, ct);
        Assert.Equal(9, ping.Length);
        await stream.WriteAsync(new byte[] { (byte)ping.Length }.Concat(ping).ToArray(), ct);
    }

    private static async Task<byte[]> ReadPacket(Stream stream, CancellationToken ct)
    {
        var length = 0;
        var next = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(next, ct);
            length |= (next[0] & 0x7f) << shift;
            if ((next[0] & 0x80) != 0) continue;
            Assert.InRange(length, 1, 1024);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, ct);
            return body;
        }
        throw new InvalidDataException();
    }
}
