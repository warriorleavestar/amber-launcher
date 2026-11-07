using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux 上自己解析 res_query 的 SRV 应答。</summary>
public sealed class SrvUnixTests
{
    private static byte[] Name(string name) =>
        [.. name.Split('.').SelectMany(l => new[] { (byte)l.Length }.Concat(System.Text.Encoding.ASCII.GetBytes(l))), 0];

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    /// <summary>拼一个应答：问题 _minecraft._tcp.example.org SRV，答案若干条（目标用压缩指针或完整名字）。</summary>
    private static byte[] Response(params (int Priority, int Port, byte[] Target)[] records)
    {
        var m = new List<byte>();
        m.AddRange(U16(0x1234)); m.AddRange(U16(0x8180)); m.AddRange(U16(1)); m.AddRange(U16(records.Length)); m.AddRange(U16(0)); m.AddRange(U16(0));
        m.AddRange(Name("_minecraft._tcp.example.org"));
        m.AddRange(U16(33)); m.AddRange(U16(1));
        foreach (var (priority, port, target) in records)
        {
            m.AddRange([0xC0, 12]); // 指回问题里的名字
            m.AddRange(U16(33)); m.AddRange(U16(1)); m.AddRange([0, 0, 1, 0]);
            m.AddRange(U16(6 + target.Length));
            m.AddRange(U16(priority)); m.AddRange(U16(5)); m.AddRange(U16(port));
            m.AddRange(target);
        }
        return [.. m];
    }

    [Fact]
    public void PicksLowestPriority()
    {
        var r = Response((20, 25570, Name("backup.example.org")), (10, 25566, Name("mc.example.org")));
        Assert.Equal(("mc.example.org", 25566), UnixResolver.ParseSrv(r));
    }

    [Fact]
    public void FollowsCompressionPointers()
    {
        // 目标 “play” + 指向问题里 “example.org” 的指针（偏移 12 + 1 + 10 + 1 + 4 = 28）。
        byte[] target = [4, (byte)'p', (byte)'l', (byte)'a', (byte)'y', 0xC0, 28];
        Assert.Equal(("play.example.org", 25565), UnixResolver.ParseSrv(Response((0, 25565, target))));
    }

    [Fact]
    public void RejectsBrokenOrEmptyAnswers()
    {
        Assert.Null(UnixResolver.ParseSrv([]));
        Assert.Null(UnixResolver.ParseSrv(Response()));
        Assert.Null(UnixResolver.ParseSrv(Response((0, 25565, [0])))); // 目标是“.”：服务不存在
        var r = Response((0, 25565, Name("mc.example.org")));
        Assert.Null(UnixResolver.ParseSrv(r.AsSpan(0, r.Length - 5)));
        // 指针指向自己：不能死循环。
        byte[] loop = [0xC0, 0];
        var looped = Response((0, 25565, loop));
        var at = looped.Length - 2;
        looped[at + 1] = (byte)at;
        Assert.Null(UnixResolver.ParseSrv(looped));
    }
}
