using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amber.App.ViewModels;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>对真正的进程和目录联接检查数据边界，所有文件都留在独立临时目录。</summary>
public sealed class ProcessBoundarySecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-process-boundary-" + Guid.NewGuid().ToString("N"));

    public ProcessBoundarySecurityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void NativeJunction_ReadsUnusualTarget_AndNeverOverwritesExistingPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_root, "目标 & %TEMP% ! ^ (x) '");
        var link = Path.Combine(_root, "junction & %TEMP% ! ^ (x) '");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original");
        WindowsJunction.Create(link, target);
        try
        {
            Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
            Assert.Equal("original", File.ReadAllText(Path.Combine(link, "keep.txt")));
            Assert.Throws<Win32Exception>(() => WindowsJunction.Create(link, target));
            Assert.Throws<Win32Exception>(() => WindowsJunction.Create(target, link));
            Assert.Equal("original", File.ReadAllText(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Theory]
    [InlineData(@"\\server\share\target")]
    [InlineData(@"\\?\UNC\server\share\target")]
    [InlineData("relative")]
    public void NativeJunction_RejectsNonLocalOrRelativeTargets(string target)
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = Path.Combine(_root, "link");
        Assert.Throws<IOException>(() => WindowsJunction.Create(link, target));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void NativeJunction_AcceptsExtendedLocalPathsAndDriveRootTargets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_root, "extended-target");
        var link = Path.Combine(_root, "extended-link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original");
        WindowsJunction.Create(@"\\?\" + link, @"\\?\" + target);
        try
        {
            Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
            Assert.Equal("original", File.ReadAllText(Path.Combine(link, "keep.txt")));
        }
        finally { Directory.Delete(link); }

        // 根目录仅作为联接目标，既不枚举也不写入；只删除临时目录内的联接本身。
        var driveRoot = Path.GetPathRoot(_root)!;
        WindowsJunction.Create(link, @"\\?\" + driveRoot);
        try
        {
            Assert.Equal(driveRoot, new DirectoryInfo(link).LinkTarget);
            Assert.True(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void AsciiPaths_PreservesDriveRootAndNormalizesExtendedAliases()
    {
        if (!OperatingSystem.IsWindows()) return;
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.Equal(driveRoot, AsciiPaths.AliasIfNeeded(driveRoot));
        Assert.Equal(driveRoot, AsciiPaths.AliasIfNeeded(@"\\?\" + driveRoot));
        Assert.Equal(driveRoot, AsciiPaths.MapArgument(driveRoot, _ => false, directory =>
        {
            Assert.Equal(driveRoot, directory);
            return directory;
        }));
        Assert.Equal(Path.Combine(_root, "a", "b"), AsciiPaths.Remap(Path.Combine(driveRoot, "a", "b"), driveRoot, _root));
    }

    [Fact]
    public async Task ExportedWindowsScript_RoundTripsArgumentsPathsAndLongClasspath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var work = Path.Combine(_root, "game & %AMBER_TEST_EXPAND% ! ^ (测试) '");
        Directory.CreateDirectory(work);
        var executable = Path.Combine(work, "java & %AMBER_TEST_EXPAND% ! ^ (测试) '.exe");
        var output = Path.Combine(_root, "arguments.bin");
        var marker = Path.Combine(_root, "injected.txt");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var compilerScript = Path.Combine(_root, "compile.ps1");
        await File.WriteAllTextAsync(compilerScript, """
            $ErrorActionPreference = 'Stop'
            Add-Type -OutputAssembly $env:AMBER_TEST_EXE -OutputType ConsoleApplication -TypeDefinition @'
            using System;
            using System.IO;
            using System.Text;
            public static class ArgumentRecorder {
                public static int Main(string[] args) {
                    using (var writer = new BinaryWriter(File.Create(Environment.GetEnvironmentVariable("AMBER_TEST_OUTPUT")), Encoding.UTF8)) {
                        writer.Write(Environment.CurrentDirectory);
                        writer.Write(args.Length);
                        foreach (var arg in args) writer.Write(arg);
                    }
                    return 23;
                }
            }
            '@
            """);
        var compiler = new ProcessStartInfo(powershell) { UseShellExecute = false };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", compilerScript }) compiler.ArgumentList.Add(arg);
        compiler.Environment["AMBER_TEST_EXE"] = executable;
        Assert.Equal(0, await RunAsync(compiler));

        string[] args = ["", "plain", "two words", "\"quoted\"", @"C:\a b\", "before\\\"after", "%AMBER_TEST_EXPAND%", "!AMBER_TEST_EXPAND!",
            "^ & | < > ( )", $"\" & echo injected>\"{marker}\" & rem ", "line1\r\nline2", "中文名字", "$(touch injected)", "a'b", "--accessToken", "secret-token", "prefix-secret-token-suffix",
            "-cp", new string('c', 12000)];
        var script = Path.Combine(work, "run & %AMBER_TEST_EXPAND% ! ^ (测试) '.bat");
        Toolbox.WriteLaunchScript(script, executable, work, args, "secret-token");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = _root,
            Arguments = "/d /v:off /s /c \"\"%AMBER_TEST_SCRIPT%\"\"",
        };
        start.Environment["AMBER_TEST_SCRIPT"] = script;
        start.Environment["AMBER_TEST_OUTPUT"] = output;
        start.Environment["AMBER_TEST_EXPAND"] = "EXPANDED_UNEXPECTEDLY";
        Assert.Equal(23, await RunAsync(start));
        AssertRecordedArguments(output, work, args.Select(a => a.Replace("secret-token", "REPLACE_WITH_TOKEN")).ToArray());
        Assert.False(File.Exists(marker));

        // 用户编辑的是实际消费的 JSON 数据；即使令牌带引号、百分号、反斜杠和 shell 符号，也只是一项参数。
        const string editedToken = "edited\" %AMBER_TEST_EXPAND% !AMBER_TEST_EXPAND! & | ^ tail\\";
        var scriptText = await File.ReadAllTextAsync(script);
        Assert.Contains("REPLACE_WITH_TOKEN", scriptText);
        Assert.DoesNotContain("secret-token", scriptText);
        await File.WriteAllTextAsync(script, scriptText.Replace("REPLACE_WITH_TOKEN", JsonEncodedText.Encode(editedToken).ToString()), new UTF8Encoding(false));
        Assert.Equal(23, await RunAsync(start));
        AssertRecordedArguments(output, work, args.Select(a => a.Replace("secret-token", editedToken)).ToArray());
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ExportedPosixScript_TreatsShellSyntaxAsData_AndChecksCdFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = Path.Combine(_root, "game ' $(touch injected) ; &");
        Directory.CreateDirectory(work);
        var recorder = Path.Combine(_root, "recorder ' ; &.sh");
        await File.WriteAllTextAsync(recorder, "#!/bin/sh\nprintf '%s\\000' \"$PWD\" \"$@\" > \"$AMBER_TEST_OUTPUT\"\n");
        File.SetUnixFileMode(recorder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] args = ["", "a'b", "$(touch injected)", "`touch injected`", "semi;colon", "a\nb", "token"];
        var script = Path.Combine(_root, "run.sh");
        var output = Path.Combine(_root, "recorded");
        Toolbox.WriteLaunchScript(script, recorder, work, args, "token");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = _root };
        start.ArgumentList.Add(script);
        start.Environment["AMBER_TEST_OUTPUT"] = output;
        Assert.Equal(0, await RunAsync(start));
        var actual = (await File.ReadAllTextAsync(output)).Split('\0');
        Assert.Equal(new[] { work }.Concat(args.Select(a => a.Replace("token", "REPLACE_WITH_TOKEN"))).Append(""), actual);
        Assert.False(File.Exists(Path.Combine(_root, "injected")));
        File.Delete(output);
        Toolbox.WriteLaunchScript(script, recorder, Path.Combine(_root, "missing"), args, "token");
        Assert.NotEqual(0, await RunAsync(start));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("java\0.exe", "work", "arg")]
    [InlineData("java", "work\0dir", "arg")]
    [InlineData("java", "work", "arg\0value")]
    public void ExportRejectsNulBeforeCreatingScript(string executable, string work, string argument)
    {
        var script = Path.Combine(_root, "run.bat");
        Assert.Throws<ArgumentException>(() => Toolbox.WriteLaunchScript(script, executable, work, [argument], ""));
        Assert.False(File.Exists(script));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\program.exe")]
    [InlineData("ms-msdt:/id example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.test/release")]
    [InlineData("https://example.test/a\r\nb")]
    [InlineData("https://example.test/a%0db")]
    [InlineData("https://example.test/a%00b")]
    [InlineData("/release")]
    [InlineData("https:\\example.test\\release")]
    public async Task UnsafeUpdateUrl_IsRejectedByFeedAndFinalOpenCommand(string url)
    {
        Assert.False(UpdateChecker.TryGetSafeUpdateUrl(url, out _));
        using var http = new HttpClient(new UpdateFeedHandler(url));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(http).CheckAsync("https://feed.example.test", "1.0"));
        // 命令无须连接 LauncherService；恶意值必须在触碰系统 shell 之前被挡住。
        var viewModel = new ToolsViewModel(null!) { UpdateUrl = url };
        viewModel.OpenUpdateUrlCommand.Execute(null);
        Assert.Null(viewModel.UpdateUrl);
        Assert.NotEmpty(viewModel.UpdateStatus);
    }

    [Theory]
    [InlineData("HTTPS://Example.TEST:443/a b?q=x#notes", "https://example.test/a%20b?q=x#notes")]
    [InlineData("http://localhost:8080/release", "http://localhost:8080/release")]
    [InlineData("http://192.168.1.2/release", "http://192.168.1.2/release")]
    [InlineData("https://example.test/release?name=%22quoted%22&x=1", "https://example.test/release?name=%22quoted%22&x=1")]
    public async Task HttpUpdateUrl_IsNormalizedAndPreservesSelfHosting(string url, string normalized)
    {
        Assert.True(UpdateChecker.TryGetSafeUpdateUrl(url, out var parsed));
        Assert.Equal(normalized, parsed!.AbsoluteUri);
        using var http = new HttpClient(new UpdateFeedHandler(url));
        var update = await new UpdateChecker(http).CheckAsync("https://feed.example.test", "1.0");
        Assert.Equal(normalized, update!.Url);
    }

    private static async Task<int> RunAsync(ProcessStartInfo start)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var errors = await stderr;
        var output = await stdout;
        Assert.True(process.ExitCode is 0 or 23 || (process.ExitCode == 1 && start.FileName == "/bin/sh"),
            $"进程退出代码 {process.ExitCode}: {errors}\n{output}");
        return process.ExitCode;
    }

    private static void AssertRecordedArguments(string path, string work, IReadOnlyList<string> expected)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        Assert.Equal(work, reader.ReadString());
        Assert.Equal(expected.Count, reader.ReadInt32());
        foreach (var argument in expected) Assert.Equal(argument, reader.ReadString());
    }

    private sealed class UpdateFeedHandler(string url) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v2.0", html_url = url })),
            });
    }
}
