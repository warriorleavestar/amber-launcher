using System.Diagnostics;
using System.IO.Compression;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Auth;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>多开时每局各自保存无痕状态，崩溃卡导出的始终是对应那一局的快照。</summary>
public sealed class HomeNoTraceRunTests
{
    [Fact]
    public void ConcurrentRunsKeepTheirOwnPrivacyAndCrashExportSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-home-notrace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings
                {
                    GameDirectory = root,
                    SavePathForTest = Path.Combine(root, "settings.json"),
                    NoTrace = true,
                });
                var home = new HomeViewModel(service, new LogViewModel());
                // 只需要进程占位，不启动 Java，也不启动任何外部进程。
                using var gameA = new Process();
                using var gameB = new Process();
                var a = home.AddRunForTest("A", gameA, Path.Combine(root, "A"));
                var b = home.AddRunForTest("B", gameB, Path.Combine(root, "B"));
                try
                {
                    HomeViewModel.BeginRunOutputForTest(a, Options("A", noTrace: true));
                    HomeViewModel.BeginRunOutputForTest(b, Options("B", noTrace: false));
                    HomeViewModel.AddRunOutputForTest(a, "<log4j:Event><log4j:Message><![CDATA[[CHAT] A private first line");
                    HomeViewModel.AddRunOutputForTest(b, "[12:00:00] [Render thread/INFO]: [CHAT] B normal first line");
                    for (var i = 0; i < RunOutput.Kept + 5; i++)
                        HomeViewModel.AddRunOutputForTest(a, "A private continuation " + i);
                    HomeViewModel.AddRunOutputForTest(b, "B normal continuation");
                    HomeViewModel.AddRunOutputForTest(a, "]]></log4j:Message></log4j:Event>");
                    HomeViewModel.AddRunOutputForTest(a, "[12:00:01] [main/ERROR]: java.lang.OutOfMemoryError");
                    HomeViewModel.AddRunOutputForTest(a, "\tat A.Main.run(Main.java:12)");

                    // 原始日志仍供本机查看和崩溃分析，无痕模式只影响报告的导出。
                    Assert.Equal(RunOutput.Kept, HomeViewModel.RunOutputForTest(a).Length);
                    Assert.Contains(HomeViewModel.RunOutputForTest(a), l => l.StartsWith("A private continuation", StringComparison.Ordinal));
                    service.Settings.NoTrace = false;
                    home.RememberCrashForTest(a);
                    Assert.NotEmpty(home.Crash!.Findings);

                    // 另一局继续输出，以及之后修改全局设置，不能替换已经显示的崩溃卡对应的输出。
                    HomeViewModel.AddRunOutputForTest(b, "B later continuation");
                    HomeViewModel.AddRunOutputForTest(a, "[12:00:02] [main/INFO]: later than crash snapshot");
                    Assert.Equal(
                        ["[12:00:01] [main/ERROR]: java.lang.OutOfMemoryError", "\tat A.Main.run(Main.java:12)"],
                        Export(home, Path.Combine(root, "a.zip")));

                    service.Settings.NoTrace = true;
                    home.RememberCrashForTest(b);
                    Assert.Equal(
                        ["[12:00:00] [Render thread/INFO]: [CHAT] B normal first line", "B normal continuation", "B later continuation"],
                        Export(home, Path.Combine(root, "b.zip")));
                }
                finally
                {
                    home.RemoveRunForTest(b);
                    home.RemoveRunForTest(a);
                }
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static LaunchOptions Options(string id, bool noTrace) => new()
    {
        VersionId = id,
        JavaPath = "java",
        Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        NoTrace = noTrace,
    };

    private static string[] Export(HomeViewModel home, string path)
    {
        home.ExportCrashBundle(path);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("launcher-output.txt")!.Open());
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }
}
