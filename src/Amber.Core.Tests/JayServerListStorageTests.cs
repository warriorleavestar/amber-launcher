using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Gate;

namespace Amber.Core.Tests;

/// <summary>jay-servers.json 里访问密钥的保存格式：Mac/Linux 上不能有明文，旧的 plain: 能读并自动迁移。</summary>
public sealed class JayServerListStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-serverlist-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static GateMembership Membership(byte[] key) =>
        new("mc.example.com", 47670, RandomNumberGenerator.GetBytes(32), key, "Steve", null);

    private string FilePath => Path.Combine(_root, JayServerList.FileName);

    [Fact]
    public void AccessKeyIsNotStoredInPlainText()
    {
        var list = new JayServerList(_root);
        var key = RandomNumberGenerator.GetBytes(32);
        list.Add(Membership(key));
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain(Convert.ToBase64String(key), text);
        Assert.DoesNotContain("plain:", text);
        var stored = list.Load().Single().ProtectedAccessKey;
        Assert.StartsWith(OperatingSystem.IsWindows() ? "dpapi:" : "aesgcm:", stored);
        Assert.Equal(key, list.ToMembership(list.Load().Single()).AccessKey);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath + ".key"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        }
    }

    [Fact]
    public void OldPlainEntryStillWorksAndIsMigrated()
    {
        var list = new JayServerList(_root);
        var key = RandomNumberGenerator.GetBytes(32);
        list.Add(Membership(key));
        // 模拟 v0.7.0 在 Mac/Linux 上写的文件：访问密钥是 plain:Base64。
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        json["Servers"]![0]!["ProtectedAccessKey"] = "plain:" + Convert.ToBase64String(key);
        File.WriteAllText(FilePath, json.ToJsonString());

        var entry = new JayServerList(_root).Load().Single();
        Assert.Equal(key, list.ToMembership(entry).AccessKey);
        if (!OperatingSystem.IsWindows())
        {
            // 读一次之后文件里已经是加密格式。
            var text = File.ReadAllText(FilePath);
            Assert.DoesNotContain("plain:", text);
            Assert.DoesNotContain(Convert.ToBase64String(key), text);
            Assert.Equal(key, list.ToMembership(list.Load().Single()).AccessKey);
        }
    }

    [Fact]
    public void TamperedOrKeylessEntryFailsInsteadOfReturningGarbage()
    {
        if (OperatingSystem.IsWindows()) return;
        var list = new JayServerList(_root);
        list.Add(Membership(RandomNumberGenerator.GetBytes(32)));
        var entry = list.Load().Single();
        var raw = Convert.FromBase64String(entry.ProtectedAccessKey["aesgcm:".Length..]);
        raw[^1] ^= 1;
        var tampered = new JayServerList.Entry { Host = entry.Host, Port = entry.Port, ServerPublicKey = entry.ServerPublicKey, Username = entry.Username, ProtectedAccessKey = "aesgcm:" + Convert.ToBase64String(raw) };
        Assert.ThrowsAny<CryptographicException>(() => list.ToMembership(tampered));
        File.Delete(FilePath + ".key");
        Assert.ThrowsAny<CryptographicException>(() => list.ToMembership(entry));
        // 密钥文件丢了也不会自动生成新的去覆盖。
        Assert.False(File.Exists(FilePath + ".key"));
    }
}
