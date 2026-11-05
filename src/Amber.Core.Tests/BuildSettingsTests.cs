using System.Xml.Linq;

namespace Amber.Core.Tests;

/// <summary>编译设置里和项目规则有关的部分。</summary>
public class BuildSettingsTests
{
    private static readonly string Src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Avalonia 的编译统计（Avalonia.BuildServices）要关掉：规则不允许遥测，而且它另起的 dotnet 进程在没有写权限时会崩、弹“应用程序错误”。
    /// 统计目标只在 UsedAvaloniaProducts 非空时运行。Avalonia 的包（含间接引用时的 buildTransitive）在 .props 里给它加值，
    /// 所以 src/Directory.Build.targets 里要清空。这里对每个直接或间接引用 Avalonia 的项目，用 MSBuild 求出最终值来检查。
    /// </summary>
    [Fact]
    public void AvaloniaBuildTelemetryIsDisabledInEveryProject()
    {
        var targets = XDocument.Load(Path.Combine(Src, "Directory.Build.targets"));
        var values = targets.Descendants("UsedAvaloniaProducts").ToList();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("", v.Value.Trim()));
        Assert.All(values, v => Assert.Null(v.Attribute("Condition")));

        var projects = AvaloniaProjects();
        Assert.Contains(projects, p => p.EndsWith("Amber.App.csproj", StringComparison.Ordinal));
        Assert.Contains(projects, p => p.EndsWith("Amber.Core.Tests.csproj", StringComparison.Ordinal));
        // 找不到 dotnet（比如只拿到编译好的测试程序集）时只做上面的静态检查。xunit 2 不能运行时跳过，就直接结束。
        if (DotnetPath() is not { } dotnet) return;
        foreach (var project in projects)
            Assert.True(EvaluatedProperty(dotnet, project, "UsedAvaloniaProducts").Length == 0, $"{Path.GetFileName(project)} 的 UsedAvaloniaProducts 不是空的");
    }

    /// <summary>直接引用 Avalonia 包，或者经项目引用间接用到它的项目。</summary>
    private static List<string> AvaloniaProjects()
    {
        var all = Directory.EnumerateFiles(Src, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(Path.GetFullPath).ToList();
        var direct = all.Where(p => XDocument.Load(p).Descendants("PackageReference")
            .Any(r => (r.Attribute("Include")?.Value ?? "").StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))).ToHashSet();
        var result = new HashSet<string>(direct);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var p in all.Where(p => !result.Contains(p)))
            {
                var refs = XDocument.Load(p).Descendants("ProjectReference")
                    .Select(r => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, r.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))));
                if (refs.Any(result.Contains)) changed = result.Add(p) || changed;
            }
        }
        return [.. result];
    }

    /// <summary>dotnet 的位置：dotnet test 会设 DOTNET_HOST_PATH，否则在 PATH 里找；找不到返回 null。</summary>
    internal static string? DotnetPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>MSBuild 求出来的属性值。stdout 和 stderr 同时读（只读一个时另一个写满缓冲会卡住），最多等两分钟。</summary>
    private static string EvaluatedProperty(string dotnet, string project, string name)
    {
        var info = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "msbuild", project, "-getProperty:" + name, "-nologo" }) info.ArgumentList.Add(a);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail($"dotnet msbuild {Path.GetFileName(project)} 两分钟没结束");
        }
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet msbuild {Path.GetFileName(project)} 退出码 {process.ExitCode}：{error.Result}");
        return output.Result.Trim();
    }

    /// <summary>
    /// App.axaml 里 Application.Resources 要在 Application.Styles 前面：样式里的 StaticResource（UiFont、FontCaption 等）
    /// 在加载时就查找，资源写在后面会在启动时抛 KeyNotFoundException。
    /// </summary>
    [Fact]
    public void AppResourcesComeBeforeStyles()
    {
        var app = XDocument.Load(Path.Combine(Src, "Amber.App", "App.axaml"));
        var names = app.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        var resources = names.IndexOf("Application.Resources");
        var styles = names.IndexOf("Application.Styles");
        Assert.True(resources >= 0 && styles >= 0);
        Assert.True(resources < styles, "Application.Resources 要写在 Application.Styles 前面");
    }
}
