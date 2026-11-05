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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiskAttachmentsStayWithCrashedRunAfterSourceFilesAreReused(bool noTrace)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-crash-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings
                {
                    GameDirectory = root,
                    SavePathForTest = Path.Combine(root, "settings.json"),
                });
                var home = new HomeViewModel(service, new LogViewModel());
                using var game = new Process();
                var run = home.AddRunForTest("A", game, root);
                try
                {
                    HomeViewModel.BeginRunOutputForTest(run, Options("A", noTrace));
                    var latest = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "logs")).FullName, "latest.log");
                    var report = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "crash-reports")).FullName, "crash-a.txt");
                    var native = Path.Combine(root, "hs_err_pid123.log");
                    File.WriteAllText(latest, "[12:00:00] [main/INFO]: A diagnostic\n[12:00:01] [Render thread/INFO]: [CHAT] A private\nA private continuation\n[12:00:02] [main/ERROR]: java.lang.OutOfMemoryError\n");
                    File.WriteAllText(report, "A crash report\njava.lang.OutOfMemoryError\n");
                    File.WriteAllText(native, "A native diagnostic\n");
                    foreach (var path in new[] { latest, report, native }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
                    home.RememberCrashForTest(run);

                    // 下一局复用了同一个目录：覆盖日志和报告，并移走 JVM 日志；导出 A 时不能读这些新内容。
                    File.WriteAllText(latest, "[12:00:03] [Render thread/INFO]: [CHAT] B private\nB private continuation\n");
                    File.WriteAllText(report, "B crash report\n");
                    File.Delete(native);
                    service.Settings.NoTrace = !noTrace;

                    var entries = ExportEntries(home, Path.Combine(root, "report.zip"));
                    Assert.Equal("A crash report\njava.lang.OutOfMemoryError\n", entries["crash-a.txt"]);
                    Assert.Equal("A native diagnostic\n", entries["hs_err_pid123.log"]);
                    Assert.Contains("A diagnostic", entries["latest.log"]);
                    Assert.Contains("java.lang.OutOfMemoryError", entries["latest.log"]);
                    Assert.Equal(!noTrace, entries["latest.log"].Contains("A private", StringComparison.Ordinal));
                    Assert.DoesNotContain(entries.Values, text => text.Contains("B private", StringComparison.Ordinal) || text.Contains("B crash", StringComparison.Ordinal));
                }
                finally
                {
                    home.RemoveRunForTest(run);
                }
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TruncatedAttachmentDoesNotLeakChatAfterItsHeaderWasDropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-crash-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings
                {
                    GameDirectory = root,
                    SavePathForTest = Path.Combine(root, "settings.json"),
                });
                var home = new HomeViewModel(service, new LogViewModel());
                using var game = new Process();
                var run = home.AddRunForTest("A", game, root);
                try
                {
                    HomeViewModel.BeginRunOutputForTest(run, Options("A", noTrace: true));
                    var latest = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "logs")).FullName, "latest.log");
                    using (var writer = new StreamWriter(latest))
                    {
                        writer.WriteLine("[12:00:00] [Render thread/INFO]: [CHAT] private first line");
                        for (var i = 0; i < HomeViewModel.MaxCrashAttachmentBytes / 10; i++) writer.WriteLine("private continuation");
                        writer.WriteLine("[12:00:01] [main/ERROR]: java.lang.OutOfMemoryError");
                        writer.WriteLine("\tat A.Main.run(Main.java:12)");
                    }
                    File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddSeconds(1));
                    Assert.True(new FileInfo(latest).Length > HomeViewModel.MaxCrashAttachmentBytes);
                    home.RememberCrashForTest(run);
                    File.Delete(latest);

                    var entries = ExportEntries(home, Path.Combine(root, "report.zip"));
                    Assert.False(entries.ContainsKey("latest.log"));
                    Assert.Equal("[12:00:01] [main/ERROR]: java.lang.OutOfMemoryError\n\tat A.Main.run(Main.java:12)\n", entries["latest.tail.log"]);
                    Assert.DoesNotContain(entries.Values, text => text.Contains("private", StringComparison.Ordinal));
                }
                finally
                {
                    home.RemoveRunForTest(run);
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

    private static Dictionary<string, string> ExportEntries(HomeViewModel home, string path)
    {
        home.ExportCrashBundle(path);
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        });
    }
}
