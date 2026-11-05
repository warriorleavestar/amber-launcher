using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Amber.Core.Localization;

/// <summary>
/// 把常见异常换成用户看得懂的一句话（按当前界面语言）：断网、超时、文件被占用、磁盘满、没有权限这类。
/// 认不出的保留异常自己的 Message。完整的异常（堆栈）放在状态条的【详情】里，不靠这里。
/// </summary>
public static class ErrorText
{
    // Windows 的 HRESULT：文件被占用、锁定冲突、磁盘满。
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    // 拒绝访问：多半是别的程序（游戏、杀毒软件）开着这个文件，或者没有权限。
    private const int AccessDenied = unchecked((int)0x80070005);

    public static string Describe(Exception e) => DescribeKey(e) is { } key ? L.T(key) : Innermost(e).Message;

    /// <summary>
    /// 放进状态消息参数用：显示时才按当时的语言翻译（Describe 返回的是现成的字符串，换语言后会混着两种语言）。
    /// </summary>
    public static object Of(Exception e) => new Deferred(e);

    private sealed class Deferred(Exception e)
    {
        public override string ToString() => Describe(e);
    }

    /// <summary>认出来的异常对应的翻译键；认不出返回 null。单独拿出来方便测试。</summary>
    public static string? DescribeKey(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            switch (x)
            {
                case TimeoutException:
                case TaskCanceledException when x.InnerException is TimeoutException:
                    return "error.timeout";
                case AuthenticationException:
                    return "error.tls";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }:
                    return "error.dns";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return "error.refused";
                case SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown }:
                    return "error.offline";
                case HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }:
                    return "error.http_rate_limited";
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                    return "error.http_not_found";
                case HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }:
                    return "error.http_server";
                case UnauthorizedAccessException:
                    return "error.access_denied";
                case IOException { HResult: SharingViolation or LockViolation }:
                    return "error.file_in_use";
                case IOException { HResult: DiskFull or HandleDiskFull }:
                    return "error.disk_full";
                case IOException { HResult: AccessDenied }:
                    return "error.in_use_or_denied";
                case PathTooLongException:
                    return "error.path_too_long";
            }
        }
        // 没有更具体的原因时：HTTP 请求整体失败（没有状态码）多半是没联网。
        return e is HttpRequestException { StatusCode: null } ? "error.network" : null;
    }

    private static Exception Innermost(Exception e)
    {
        // AggregateException、TargetInvocation 这类包装层的 Message 没信息量，取里面那个。
        while (e is AggregateException { InnerExceptions.Count: 1 } && e.InnerException is not null) e = e.InnerException;
        return e;
    }
}
