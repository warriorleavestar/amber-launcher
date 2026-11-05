using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

public class ErrorTextTests
{
    public static TheoryData<Exception, string?> Cases => new()
    {
        { new TimeoutException(), "error.timeout" },
        { new TaskCanceledException("t", new TimeoutException()), "error.timeout" },
        { new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), "error.dns" },
        { new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), "error.refused" },
        { new HttpRequestException("x", new SocketException((int)SocketError.NetworkUnreachable)), "error.offline" },
        { new HttpRequestException("x", new AuthenticationException()), "error.tls" },
        { new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), "error.http_rate_limited" },
        { new HttpRequestException("x", null, HttpStatusCode.NotFound), "error.http_not_found" },
        { new HttpRequestException("x", null, HttpStatusCode.BadGateway), "error.http_server" },
        { new HttpRequestException("x"), "error.network" },
        { new UnauthorizedAccessException(), "error.access_denied" },
        { new IOException("x", unchecked((int)0x80070020)), "error.file_in_use" },
        { new IOException("x", unchecked((int)0x80070070)), "error.disk_full" },
        { new PathTooLongException(), "error.path_too_long" },
        { new AggregateException(new UnauthorizedAccessException()), "error.access_denied" },
        { new InvalidOperationException("something specific"), null },
        { new HttpRequestException("x", null, HttpStatusCode.Forbidden), null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RecognisesCommonFailures(Exception e, string? key) => Assert.Equal(key, ErrorText.DescribeKey(e));

    [Fact]
    public void UnknownExceptionsKeepTheirOwnMessage()
    {
        Assert.Equal("something specific", ErrorText.Describe(new InvalidOperationException("something specific")));
        Assert.Equal("inner", ErrorText.Describe(new AggregateException(new InvalidOperationException("inner"))));
    }

    [Fact]
    public void EveryKeyExistsInEveryLanguage()
    {
        var keys = Cases.Select(c => (string?)c[1]).OfType<string>().Distinct();
        foreach (var lang in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.Core", "Localization", lang + ".json"));
            foreach (var key in keys) Assert.Contains('"' + key + '"', json);
        }
    }
}
