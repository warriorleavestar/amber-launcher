using System.Net;
using System.Text;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>按地址返回预设响应的假 HTTP，并记录请求体。</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode, string, Dictionary<string, string>?)>> _routes = [];
    public List<(string Url, string Body)> Requests { get; } = [];

    public FakeHttp On(string url, string body, HttpStatusCode code = HttpStatusCode.OK, Dictionary<string, string>? headers = null)
    {
        if (!_routes.TryGetValue(url, out var q)) _routes[url] = q = new();
        q.Enqueue((code, body, headers));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((url, body));
        if (!_routes.TryGetValue(url, out var q) || q.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        var (code, text, headers) = q.Count > 1 ? q.Dequeue() : q.Peek();
        var response = new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json"), RequestMessage = request };
        if (headers is not null)
            foreach (var (k, v) in headers) response.Headers.TryAddWithoutValidation(k, v);
        return response;
    }
}

public class AuthTests
{
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private static FakeHttp XboxChain() => new FakeHttp()
        .On("https://user.auth.xboxlive.com/user/authenticate", """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"hash1"}]}}""")
        .On("https://xsts.auth.xboxlive.com/xsts/authorize", """{"Token":"xsts","DisplayClaims":{"xui":[{"uhs":"hash1","xid":"2535"}]}}""")
        .On("https://api.minecraftservices.com/authentication/login_with_xbox", """{"access_token":"mc-token","expires_in":86400}""")
        .On("https://api.minecraftservices.com/minecraft/profile", """{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch","skins":[{"state":"ACTIVE","url":"https://textures.minecraft.net/texture/abc","variant":"SLIM"}]}""");

    [Fact]
    public async Task Microsoft_DeviceCode_FullChain()
    {
        var fake = XboxChain()
            .On("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", """{"user_code":"ABCD","device_code":"dc","verification_uri":"https://microsoft.com/link","interval":1,"expires_in":60,"message":"m"}""")
            .On(TokenUrl, """{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
            .On(TokenUrl, """{"access_token":"ms","refresh_token":"rt"}""");
        var auth = new MicrosoftAuth(new HttpClient(fake), "my-client");

        var code = await auth.BeginDeviceCodeAsync();
        Assert.Equal("ABCD", code.UserCode);
        var account = await auth.CompleteDeviceCodeAsync(code);

        Assert.Equal("Notch", account.Name);
        Assert.Equal(Guid.Parse("069a79f444e94726a5befca90e38aaf5"), account.Uuid);
        Assert.Equal("mc-token", account.AccessToken);
        Assert.Equal("rt", account.RefreshToken);
        Assert.Equal("2535", account.Xuid);
        Assert.True(account.SlimSkin);
        // 用的是用户自己的 Client ID，XSTS 用户串拼对了
        Assert.Contains(fake.Requests, r => r.Url.EndsWith("/devicecode") && r.Body.Contains("client_id=my-client"));
        Assert.Contains(fake.Requests, r => r.Url.EndsWith("login_with_xbox") && r.Body.Contains("XBL3.0 x=hash1;xsts"));
    }

    [Fact]
    public async Task Microsoft_NoXboxProfile_GivesReadableError()
    {
        var fake = new FakeHttp()
            .On("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", """{"access_token":"ms","refresh_token":"rt"}""")
            .On("https://user.auth.xboxlive.com/user/authenticate", """{"Token":"xbl","DisplayClaims":{"xui":[{"uhs":"h"}]}}""")
            .On("https://xsts.auth.xboxlive.com/xsts/authorize", """{"XErr":2148916233}""", HttpStatusCode.Unauthorized);
        var account = new Account { Kind = AccountKind.Microsoft, Name = "x", RefreshToken = "old" };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new MicrosoftAuth(new HttpClient(fake), "c").RefreshAsync(account));
        Assert.Contains("Xbox 档案", e.Message);
    }

    [Fact]
    public async Task Microsoft_Refresh_SkipsWhenTokenStillValid_AndDemandsLoginWhenRevoked()
    {
        var fresh = new Account { Kind = AccountKind.Microsoft, AccessToken = "a", RefreshToken = "r", AccessTokenExpires = DateTimeOffset.UtcNow.AddHours(5) };
        var none = new FakeHttp();
        await new MicrosoftAuth(new HttpClient(none), "c").RefreshAsync(fresh);
        Assert.Empty(none.Requests);

        var expired = new Account { Kind = AccountKind.Microsoft, Name = "x", AccessToken = "a", RefreshToken = "r", AccessTokenExpires = DateTimeOffset.UtcNow.AddHours(-1) };
        var revoked = new FakeHttp().On(TokenUrl, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<LoginRequiredException>(() => new MicrosoftAuth(new HttpClient(revoked), "c").RefreshAsync(expired));
    }

    [Fact]
    public async Task External_ResolvesApiLocation_AndLogsIn_WithMultipleProfiles()
    {
        const string api = "https://skin.example/api/yggdrasil";
        var fake = new FakeHttp()
            .On("https://skin.example/", "<html>", headers: new() { ["X-Authlib-Injector-API-Location"] = "/api/yggdrasil" })
            .On(api + "/", """{"meta":{"serverName":"测试皮肤站"}}""")
            .On(api + "/authserver/authenticate", """{"accessToken":"t1","clientToken":"c1","availableProfiles":[{"id":"00000000000000000000000000000001","name":"A"},{"id":"00000000000000000000000000000002","name":"B"}]}""")
            .On(api + "/authserver/refresh", """{"accessToken":"t2","clientToken":"c1"}""");
        var auth = new ExternalAuth(new HttpClient(fake));

        Assert.Equal(api, await auth.ResolveApiRootAsync("skin.example/"));
        var result = await auth.LoginAsync(api, "me@example.com", "pw");
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("测试皮肤站", result.Account.ServerName);
        Assert.Equal(Guid.Empty, result.Account.Uuid);

        await auth.SelectProfileAsync(result.Account, result.Profiles[1]);
        Assert.Equal("B", result.Account.Name);
        Assert.Equal("t2", result.Account.AccessToken);
    }

    [Fact]
    public void AuthlibArgs_ContainAgentAndPrefetchedMetadata()
    {
        var args = AuthlibInjector.JvmArgs("C:\\a.jar", "https://x/api", "{\"a\":1}");
        Assert.Equal("-javaagent:C:\\a.jar=https://x/api", args[0]);
        Assert.EndsWith(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":1}")), args[2]);
    }

    [Fact]
    public void AccountStore_EncryptsTokensAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "amber-acc-" + Guid.NewGuid().ToString("N"), "accounts.json");
        try
        {
            var store = new AccountStore(path);
            store.AddOrUpdate(new Account { Kind = AccountKind.Microsoft, Name = "A", Uuid = Guid.NewGuid(), AccessToken = "secret-access", RefreshToken = "secret-refresh" });
            var offline = new Account { Kind = AccountKind.Offline, Name = "B", Uuid = OfflineAccount.OfflineUuid("B") };
            store.AddOrUpdate(offline);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("secret-access", raw);
            Assert.DoesNotContain("secret-refresh", raw);

            var loaded = new AccountStore(path);
            loaded.Load();
            Assert.Equal(2, loaded.Accounts.Count);
            Assert.Equal("B", loaded.Selected!.Name);
            Assert.Equal("secret-refresh", loaded.Accounts[0].RefreshToken);

            // 同一个账号再登录是更新，不是新增
            loaded.AddOrUpdate(new Account { Kind = AccountKind.Offline, Name = "B", Uuid = offline.Uuid });
            Assert.Equal(2, loaded.Accounts.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
