using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public class ProjectLinkSecurityTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\program.exe")]
    [InlineData("ms-msdt:/id example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.test/project")]
    [InlineData("https://example.test/a\r\nb")]
    [InlineData("https://example.test/a%0db")]
    [InlineData("https://example.test/a%00b")]
    [InlineData("/project")]
    [InlineData("https:\\example.test\\project")]
    public async Task UnsafeProjectLink_IsRejectedByProviderAndFinalDispatch(string value)
    {
        using var http = new HttpClient(new ProjectHandler(value));
        var provider = new CurseForgeProvider(http, () => null, "https://proxy.example.test/v1");
        var project = await provider.GetProjectAsync("123");
        Assert.Null(project.PageUrl);

        // 绕过提供方，直接构造恶意页面值，最终执行入口仍必须拦截。
        var invoked = false;
        Assert.False(ContentViewModel.TryOpenProjectPage(value, _ => invoked = true));
        Assert.False(invoked);
        Assert.False(WebLinks.TryGetHttpUrl(value, out _));
        Assert.False(UpdateChecker.TryGetSafeUpdateUrl(value, out _));
    }

    [Theory]
    [InlineData("HTTPS://Example.TEST:443/a b?q=x#notes", "https://example.test/a%20b?q=x#notes")]
    [InlineData("http://localhost:8080/project", "http://localhost:8080/project")]
    [InlineData("http://192.168.1.2/project", "http://192.168.1.2/project")]
    [InlineData("https://example.test/project?name=%22quoted%22&x=1", "https://example.test/project?name=%22quoted%22&x=1")]
    public async Task WebProjectLink_IsNormalizedWithoutLosingSelfHosting(string value, string expected)
    {
        using var http = new HttpClient(new ProjectHandler(value));
        var provider = new CurseForgeProvider(http, () => null, "https://proxy.example.test/v1");
        var project = await provider.GetProjectAsync("123");
        Assert.Equal(expected, project.PageUrl);

        ProcessStartInfo? captured = null;
        Assert.True(ContentViewModel.TryOpenProjectPage(project.PageUrl, start => captured = start));
        Assert.NotNull(captured);
        Assert.Equal(expected, captured.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Empty(captured.Arguments);
        Assert.Empty(captured.ArgumentList);
        Assert.True(UpdateChecker.TryGetSafeUpdateUrl(value, out var update));
        Assert.Equal(expected, update!.AbsoluteUri);
    }

    [Fact]
    public void MissingProjectLink_DoesNotInvokeShell()
    {
        Assert.False(ContentViewModel.TryOpenProjectPage(null, _ => Assert.Fail("缺少链接时不应执行")));
    }

    private sealed class ProjectHandler(string pageUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://proxy.example.test/v1/mods/123", request.RequestUri!.AbsoluteUri);
            var json = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["id"] = 123,
                    ["name"] = "示例项目",
                    ["links"] = new JsonObject { ["websiteUrl"] = pageUrl },
                },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
