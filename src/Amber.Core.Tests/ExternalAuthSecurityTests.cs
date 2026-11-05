using System.Net;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>R8：外置登录的服务器地址只能是 https（本机回环例外），跳转和响应头不能把它换成 http 或换到别的服务器。</summary>
public sealed class ExternalAuthSecurityTests
{
    /// <summary>模拟服务器：可以假装请求被跳转到了 redirectTo。</summary>
    private sealed class Server(Func<HttpRequestMessage, (Uri? RedirectTo, string Body, Dictionary<string, string>? Headers)> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var (redirect, body, headers) = respond(request);
            if (redirect is not null) request.RequestUri = redirect;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
            foreach (var (k, v) in headers ?? []) response.Headers.TryAddWithoutValidation(k, v);
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData("http://skin.example/api")]
    [InlineData("ftp://skin.example/api")]
    [InlineData("https://user:pw@skin.example/api")]
    [InlineData("not a url")]
    public void InsecureAddressesAreRejected(string url) => Assert.Throws<InvalidOperationException>(() => ExternalAuth.CheckSecure(url));

    [Theory]
    [InlineData("https://skin.example/api")]
    [InlineData("http://127.0.0.1:8080/api")]
    [InlineData("http://localhost:8080/api")]
    [InlineData("http://[::1]:8080/api")]
    public void SecureOrLoopbackAddressesAreAllowed(string url) => ExternalAuth.CheckSecure(url);

    [Fact]
    public async Task HttpInputIsRejectedBeforeAnyRequest()
    {
        var server = new Server(_ => (null, "", null));
        var auth = new ExternalAuth(new HttpClient(server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ResolveApiRootAsync("http://skin.example/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.LoginAsync("http://skin.example/api", "u", "password"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ApiLocationHeaderCannotDowngrade()
    {
        var server = new Server(_ => (null, "", new() { ["X-Authlib-Injector-API-Location"] = "http://evil.example/api" }));
        var auth = new ExternalAuth(new HttpClient(server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ResolveApiRootAsync("https://skin.example/"));
    }

    [Fact]
    public async Task RedirectToHttpIsRejected()
    {
        var server = new Server(_ => (new Uri("http://skin.example/"), "", null));
        var auth = new ExternalAuth(new HttpClient(server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ResolveApiRootAsync("https://skin.example/"));
    }

    [Fact]
    public async Task PasswordIsNotFollowedToAnotherServer()
    {
        var server = new Server(r => r.RequestUri!.AbsolutePath.EndsWith("/authenticate", StringComparison.Ordinal)
            ? (new Uri("https://evil.example/authserver/authenticate"), """{"accessToken":"t","availableProfiles":[{"id":"00000000000000000000000000000001","name":"A"}]}""", null)
            : (null, """{"meta":{}}""", null));
        var auth = new ExternalAuth(new HttpClient(server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.LoginAsync("https://skin.example/api", "u", "password"));
    }
}
