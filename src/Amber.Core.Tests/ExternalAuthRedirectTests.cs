using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>
/// R8：用真的本机 HTTP 服务器测跳转。以前的检查在 POST 之后才看最终地址，自动跟随 307/308 时密码已经先发到了别的服务器。
/// 外置登录现在用不自动跟随的 HttpClient：任何 3xx 都当错误，只有发现 API 地址的 GET 自己一步步跟，并且每步都查同源、不降级。
/// </summary>
public sealed class ExternalAuthRedirectTests
{
    /// <summary>极简 HTTP/1.1 服务器：每个连接只处理一个请求，记下请求行和请求体。</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<string, string, (int Status, Dictionary<string, string> Headers, string Body)> _respond;

        public List<(string Line, string Body)> Requests { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Root => $"http://127.0.0.1:{Port}";

        public TestServer(Func<string, string, (int, Dictionary<string, string>, string)> respond)
        {
            _respond = respond;
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    try { await HandleAsync(client); }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8)) < 0)
            {
                var n = await stream.ReadAsync(chunk, _cts.Token);
                if (n == 0) return;
                buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            }
            var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
            var length = head.Skip(1).Select(h => h.Split(':', 2))
                .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(p => int.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            var chunked = head.Any(h => h.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && h.Contains("chunked", StringComparison.OrdinalIgnoreCase));
            bool Complete() => chunked
                ? IndexOf(buffer.GetRange(headerEnd + 4, buffer.Count - headerEnd - 4), "0\r\n\r\n"u8) >= 0
                : buffer.Count >= headerEnd + 4 + length;
            while (!Complete())
            {
                var n = await stream.ReadAsync(chunk, _cts.Token);
                if (n == 0) break;
                buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            }
            // 分块编码的请求体不解码，直接按原文记下（测试只查里面有没有密码）。
            var body = Encoding.UTF8.GetString(buffer.Skip(headerEnd + 4).Take(chunked ? int.MaxValue : length).ToArray());
            lock (Requests) Requests.Add((head[0], body));

            var path = head[0].Split(' ')[1];
            var (status, headers, text) = _respond(path, body);
            var payload = Encoding.UTF8.GetBytes(text);
            var response = new StringBuilder($"HTTP/1.1 {status} X\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n");
            foreach (var (k, v) in headers) response.Append(k).Append(": ").Append(v).Append("\r\n");
            response.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _cts.Token);
            await stream.WriteAsync(payload, _cts.Token);
        }

        private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
        {
            for (var i = 0; i + pattern.Length <= data.Count; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
                if (match) return i;
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }

    private static (int, Dictionary<string, string>, string) Json(string body) => (200, [], body);
    private static (int, Dictionary<string, string>, string) Redirect(int status, string to) => (status, new() { ["Location"] = to }, "");

    private const string Profiles = """{"accessToken":"t","clientToken":"c","availableProfiles":[{"id":"00000000000000000000000000000001","name":"A"}],"selectedProfile":{"id":"00000000000000000000000000000001","name":"A"}}""";

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PasswordNeverReachesRedirectTarget(int status)
    {
        await using var evil = new TestServer((_, _) => Json(Profiles));
        await using var skin = new TestServer((path, _) => path.EndsWith("/authenticate", StringComparison.Ordinal)
            ? Redirect(status, evil.Root + "/api/authserver/authenticate")
            : Json("""{"meta":{}}"""));

        using var http = ExternalAuth.CreateHttpClient();
        var auth = new ExternalAuth(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.LoginAsync(skin.Root + "/api", "u", "secret-password"));
        Assert.Empty(evil.Requests);

        // 对照：自动跟随跳转的 HttpClient 确实会把密码发过去（说明这个测试真的测到了要防的事）。
        using var following = new HttpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ExternalAuth(following).LoginAsync(skin.Root + "/api", "u", "secret-password"));
        Assert.Contains(evil.Requests, r => r.Body.Contains("secret-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokensAreNotFollowedOnValidateOrRefresh()
    {
        await using var evil = new TestServer((_, _) => Json(Profiles));
        await using var skin = new TestServer((_, _) => Redirect(307, evil.Root + "/x"));
        using var http = ExternalAuth.CreateHttpClient();
        var auth = new ExternalAuth(http);
        var account = new Account { Kind = AccountKind.External, Name = "A", ServerUrl = skin.Root + "/api", AccessToken = "token-1", ClientToken = "c" };
        // validate 的 3xx 是错误，不会当成“令牌失效”接着去 refresh。
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.EnsureValidAsync(account));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => auth.SelectProfileAsync(account, new ExternalProfile("00000000000000000000000000000001", "A")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.LoadSkinAsync(account));
        Assert.Empty(evil.Requests);
        Assert.Equal(3, skin.Requests.Count);
    }

    [Fact]
    public async Task DiscoveryFollowsSameOriginHopsOnly()
    {
        await using var evil = new TestServer((_, _) => (200, new() { ["X-Authlib-Injector-API-Location"] = "/api" }, ""));
        await using var skin = new TestServer((path, _) => path switch
        {
            "/" => Redirect(302, "/landing"),
            "/landing" => (200, new() { ["X-Authlib-Injector-API-Location"] = "/api/yggdrasil" }, ""),
            "/away" => Redirect(302, evil.Root + "/"),
            _ => Redirect(301, "/loop"),
        });
        using var http = ExternalAuth.CreateHttpClient();
        var auth = new ExternalAuth(http);

        Assert.Equal(skin.Root + "/api/yggdrasil", await auth.ResolveApiRootAsync(skin.Root + "/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ResolveApiRootAsync(skin.Root + "/away"));
        Assert.Empty(evil.Requests);
        // 跳转转圈：跟到上限就停。
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ResolveApiRootAsync(skin.Root + "/loop"));
    }

    [Theory]
    [InlineData("http://loopback/api")]
    [InlineData("http://LOOPBACK:8080/api")]
    [InlineData("http://localhost.example/api")]
    [InlineData("http://127.0.0.1.example/api")]
    [InlineData("http://0.0.0.0/api")]
    public void OnlyRealLoopbackIsExempt(string url) => Assert.Throws<InvalidOperationException>(() => ExternalAuth.CheckSecure(url));

    [Theory]
    [InlineData("http://127.0.0.2:8080/api")]
    [InlineData("http://LocalHost/api")]
    public void LoopbackRangeIsExempt(string url) => ExternalAuth.CheckSecure(url);

    [Fact]
    public void SavedHttpAccountIsFlagged()
    {
        Assert.True(ExternalAuth.HasInsecureServer(new Account { Kind = AccountKind.External, Name = "A", ServerUrl = "http://skin.example/api" }));
        Assert.False(ExternalAuth.HasInsecureServer(new Account { Kind = AccountKind.External, Name = "A", ServerUrl = "https://skin.example/api" }));
        Assert.False(ExternalAuth.HasInsecureServer(new Account { Kind = AccountKind.Offline, Name = "A" }));
    }
}
