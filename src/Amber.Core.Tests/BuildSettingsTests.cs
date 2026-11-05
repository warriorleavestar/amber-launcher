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
        foreach (var project in projects)
            Assert.True(EvaluatedProperty(project, "UsedAvaloniaProducts").Length == 0, $"{Path.GetFileName(project)} 的 UsedAvaloniaProducts 不是空的");
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

    private static string EvaluatedProperty(string project, string name)
    {
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "msbuild", project, "-getProperty:" + name, "-nologo" }) info.ArgumentList.Add(a);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
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
