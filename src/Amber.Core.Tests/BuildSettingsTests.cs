using System.Xml.Linq;

namespace Amber.Core.Tests;

/// <summary>编译设置里和项目规则有关的部分。</summary>
public class BuildSettingsTests
{
    private static readonly string Src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Avalonia 的编译统计（Avalonia.BuildServices）要关掉：规则不允许遥测，而且它另起的 dotnet 进程在没有写权限时会崩、弹“应用程序错误”。
    /// 统计目标只在 UsedAvaloniaProducts 非空时运行，所以项目文件里要把它清空。
    /// </summary>
    [Fact]
    public void AvaloniaBuildTelemetryIsDisabled()
    {
        var project = XDocument.Load(Path.Combine(Src, "Amber.App", "Amber.App.csproj"));
        var values = project.Descendants("UsedAvaloniaProducts").ToList();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("", v.Value.Trim()));
        Assert.All(values, v => Assert.Null(v.Attribute("Condition")));
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
