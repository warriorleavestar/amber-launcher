using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Auth;
using Amber.Core.Diagnostics;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>无痕模式下导出错误报告：聊天去掉，Amber 自己的行留着；启动后再改设置，这一局的结果不变。</summary>
public sealed class RunOutputTests
{
    private static LaunchOptions Options(bool noTrace) => new()
    {
        VersionId = "x",
        JavaPath = "java",
        Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        NoTrace = noTrace,
    };

    private static readonly string[] Lines =
    [
        "[12:00:00] [main/INFO]: start",
        "[12:00:01] [Render thread/INFO]: [System] [CHAT] <Steve> secret plan",
        "[12:00:02] [Server thread/INFO]: [Steve] said via /say",
        "[Amber] 游戏输出里有 [CHAT] 字样",
        "crash here",
    ];

    private static List<string> Exported(RunOutput output)
    {
        var zip = Path.Combine(Path.GetTempPath(), "amber-runoutput-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            CrashAnalyzer.ExportBundle(zip, [], output.ForExport());
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("launcher-output.txt")!.Open());
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    [Fact]
    public void NoTraceRunDropsChatEvenIfSettingChangesLater()
    {
        var settings = new Amber.App.Services.AppSettings { NoTrace = true };
        var output = new RunOutput();
        output.Begin(Options(settings.NoTrace));
        foreach (var l in Lines) output.Add(l);
        settings.NoTrace = false; // 启动后关掉无痕模式：这一局照样按无痕导出
        Assert.Equal(["[12:00:00] [main/INFO]: start", "[Amber] 游戏输出里有 [CHAT] 字样", "crash here"], Exported(output));
    }

    [Fact]
    public void NormalRunKeepsEverythingEvenIfSettingChangesLater()
    {
        var settings = new Amber.App.Services.AppSettings { NoTrace = false };
        var output = new RunOutput();
        output.Begin(Options(settings.NoTrace));
        foreach (var l in Lines) output.Add(l);
        settings.NoTrace = true;
        Assert.Equal(Lines, Exported(output));
    }

    [Fact]
    public void KeepsOnlyRecentLines()
    {
        var output = new RunOutput();
        output.Begin(Options(false));
        for (var i = 0; i < RunOutput.Kept + 10; i++) output.Add("line " + i);
        Assert.Equal(RunOutput.Kept, output.Lines.Length);
        Assert.Equal("line 10", output.Lines[0]);
        output.Begin(Options(true));
        Assert.Empty(output.Lines);
        Assert.True(output.NoTrace);
    }
}
