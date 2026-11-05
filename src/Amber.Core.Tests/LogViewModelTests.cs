using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>直接提交一批日志，检查过滤、保留上限和未读状态，不启动窗口或修改游戏目录。</summary>
[Collection("GlobalLanguage")]
public class LogViewModelTests
{
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void TimestampFormattingUsesTheSelectedLanguageWithoutChangingTheRecordedTime(string language)
    {
        var originalLanguage = L.Language;
        var timestamp = new DateTimeOffset(2026, 11, 5, 16, 7, 9, TimeSpan.FromHours(13));
        var originalTime = timestamp.DateTime;
        var originalOffset = timestamp.Offset;
        try
        {
            L.SetLanguage(language);
            Assert.Equal(timestamp.ToString("T", L.Culture), Fmt.Time(timestamp));
            Assert.Equal(originalTime, timestamp.DateTime);
            Assert.Equal(originalOffset, timestamp.Offset);
        }
        finally
        {
            L.SetLanguage(originalLanguage);
        }
    }

    [Fact]
    public void RawAndStructuredLogsUseTheSameSeverityRules() => UiTestThread.Run(RawAndStructuredLogsUseTheSameSeverityRulesOnUiThread);

    private static void RawAndStructuredLogsUseTheSameSeverityRulesOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[12:34:56] [Render thread/WARN]: warning");
        vm.AddRaw("Caused by: java.lang.IllegalStateException: failed");
        vm.Add(new GameLogLine(DateTimeOffset.Now, GameLogLevel.Fatal, "main", "structured fatal"));
        vm.Add(new GameLogLine(DateTimeOffset.Now, GameLogLevel.Info, "main", "[16:00:00 DEBUG] detail"));
        vm.Flush();

        Assert.Equal([GameLogLevel.Warn, GameLogLevel.Error, GameLogLevel.Fatal, GameLogLevel.Debug],
            vm.Lines.Select(e => e.Severity));
        Assert.Equal(2, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);
    }

    [Fact]
    public void FilteringKeepsCountsAndRestoresAllRetainedRows() => UiTestThread.Run(FilteringKeepsCountsAndRestoresAllRetainedRowsOnUiThread);

    private static void FilteringKeepsCountsAndRestoresAllRetainedRowsOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/INFO]: ready");
        vm.AddRaw("[main/WARN]: warning");
        vm.AddRaw("[main/ERROR]: failed");
        vm.Flush();

        vm.OnlyProblems = true;
        Assert.Equal([GameLogLevel.Warn, GameLogLevel.Error], vm.Lines.Select(e => e.Severity));
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);

        vm.AddRaw("[main/DEBUG]: detail");
        vm.AddRaw("[main/FATAL]: stopped");
        vm.Flush();
        Assert.Equal(3, vm.Lines.Count);
        Assert.Equal(2, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);

        vm.OnlyProblems = false;
        Assert.Equal(5, vm.Lines.Count);
        Assert.Equal("[main/INFO]: ready", vm.Lines[0].Message);
        Assert.Equal("[main/FATAL]: stopped", vm.Lines[^1].Message);
    }

    [Fact]
    public void EvictedProblemsDisappearEvenWhileTheFilterIsEnabled() => UiTestThread.Run(EvictedProblemsDisappearEvenWhileTheFilterIsEnabledOnUiThread);

    private static void EvictedProblemsDisappearEvenWhileTheFilterIsEnabledOnUiThread()
    {
        var vm = new LogViewModel { OnlyProblems = true };
        vm.AddRaw("[main/WARN]: old warning");
        vm.AddRaw("[main/ERROR]: old error");
        vm.Flush();
        Assert.Equal(2, vm.Lines.Count);

        for (var i = 0; i < 5000; i++) vm.AddRaw($"[main/INFO]: newer {i}");
        vm.Flush();

        Assert.Empty(vm.Lines);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.Equal(L.T("log.empty_filtered"), vm.EmptyMessage);
        vm.OnlyProblems = false;
        Assert.Equal(5000, vm.Lines.Count);
        Assert.Equal("[main/INFO]: newer 0", vm.Lines[0].Message);
        Assert.Equal("[main/INFO]: newer 4999", vm.Lines[^1].Message);
    }

    [Fact]
    public void OversizedBatchRetainsOnlyTheNewestRows() => UiTestThread.Run(OversizedBatchRetainsOnlyTheNewestRowsOnUiThread);

    private static void OversizedBatchRetainsOnlyTheNewestRowsOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/ERROR]: evicted error");
        vm.AddRaw("[main/WARN]: evicted warning");
        for (var i = 0; i < 5000; i++) vm.AddRaw($"[main/INFO]: retained {i}");
        vm.Flush();

        Assert.Equal(5000, vm.Lines.Count);
        Assert.Equal("[main/INFO]: retained 0", vm.Lines[0].Message);
        Assert.Equal("[main/INFO]: retained 4999", vm.Lines[^1].Message);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
    }

    [Fact]
    public void BadgeCountsOnlyErrorsReceivedWhileAwayAndResetsOnEntry() => UiTestThread.Run(BadgeCountsOnlyErrorsReceivedWhileAwayAndResetsOnEntryOnUiThread);

    private static void BadgeCountsOnlyErrorsReceivedWhileAwayAndResetsOnEntryOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/WARN]: warning");
        vm.AddRaw("[main/INFO]: ready");
        vm.Flush();
        Assert.False(vm.HasBadge);

        vm.AddRaw("[main/ERROR]: first error");
        vm.AddRaw("[main/FATAL]: stopped");
        vm.Flush();
        Assert.Equal(2, vm.Badge);
        Assert.True(vm.HasBadge);

        vm.IsCurrent = true;
        Assert.Equal(0, vm.Badge);
        Assert.False(vm.HasBadge);
        vm.AddRaw("[main/ERROR]: visible error");
        vm.Flush();
        Assert.Equal(0, vm.Badge);
        Assert.Equal(3, vm.ErrorCount);

        vm.IsCurrent = false;
        vm.AddRaw("[main/ERROR]: unread error");
        vm.Flush();
        Assert.Equal(1, vm.Badge);
    }

    [Fact]
    public void EnteringBeforeABatchIsShownDoesNotLeaveAnUnreadBadge() => UiTestThread.Run(EnteringBeforeABatchIsShownDoesNotLeaveAnUnreadBadgeOnUiThread);

    private static void EnteringBeforeABatchIsShownDoesNotLeaveAnUnreadBadgeOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/ERROR]: queued error");
        vm.IsCurrent = true;
        vm.Flush();

        Assert.Single(vm.Lines);
        Assert.Equal(1, vm.ErrorCount);
        Assert.False(vm.HasBadge);
    }

    [Fact]
    public void ClearDropsRetainedAndQueuedErrorsButKeepsSubsequentOutput() => UiTestThread.Run(ClearDropsRetainedAndQueuedErrorsButKeepsSubsequentOutputOnUiThread);

    private static void ClearDropsRetainedAndQueuedErrorsButKeepsSubsequentOutputOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/ERROR]: retained error");
        vm.AddRaw("[main/WARN]: retained warning");
        vm.Flush();
        vm.AddRaw("[main/ERROR]: queued error");
        vm.Clear();
        vm.AddRaw("[main/INFO]: next launch");
        vm.Flush();

        Assert.Equal("[main/INFO]: next launch", Assert.Single(vm.Lines).Message);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.False(vm.HasBadge);
    }

    [Fact]
    public void CountChangesNotifyTheBoundSummaries() => UiTestThread.Run(CountChangesNotifyTheBoundSummariesOnUiThread);

    private static void CountChangesNotifyTheBoundSummariesOnUiThread()
    {
        var vm = new LogViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.AddRaw("[main/WARN]: warning");
        vm.AddRaw("[main/ERROR]: error");
        vm.Flush();

        Assert.Equal(L.F("log.errors", 1), vm.ErrorSummary);
        Assert.Equal(L.F("log.warnings", 1), vm.WarningSummary);
        foreach (var property in new[] { nameof(vm.ErrorCount), nameof(vm.WarningCount), nameof(vm.ErrorSummary), nameof(vm.WarningSummary) })
            Assert.True(changed.Contains(property) || changed.Contains(null) || changed.Contains(""), property);
    }

    [Fact]
    public void NextErrorCyclesThroughErrorsAndWraps() => UiTestThread.Run(NextErrorCyclesThroughErrorsAndWrapsOnUiThread);

    private static void NextErrorCyclesThroughErrorsAndWrapsOnUiThread()
    {
        var vm = new LogViewModel();
        vm.AddRaw("[main/INFO]: a");
        vm.AddRaw("[main/ERROR]: first");
        vm.AddRaw("[main/WARN]: warn");
        vm.AddRaw("[main/ERROR]: second");
        vm.Flush();
        var jumps = new List<string>();
        vm.JumpRequested += e => jumps.Add(e.Message);

        vm.NextErrorCommand.Execute(null);
        vm.NextErrorCommand.Execute(null);
        vm.NextErrorCommand.Execute(null);
        vm.NextWarningCommand.Execute(null);

        Assert.Equal(["[main/ERROR]: first", "[main/ERROR]: second", "[main/ERROR]: first", "[main/WARN]: warn"], jumps);
        Assert.Equal("[main/WARN]: warn", vm.SelectedLine?.Message);
        Assert.False(vm.FollowLatest);
        Assert.True(vm.HasErrors);
    }

    [Fact]
    public void NextMatchHandlesEmptyAndMissingCurrent() => UiTestThread.Run(() =>
    {
        Assert.Null(LogViewModel.NextMatch([], null, _ => true));
        var vm = new LogViewModel();
        vm.AddRaw("[main/INFO]: only info");
        vm.Flush();
        Assert.False(vm.HasErrors);
        vm.NextErrorCommand.Execute(null);
        Assert.Null(vm.SelectedLine);
    });

    [Fact]
    public void RunSummaryShowsInstanceAndStartTime() => UiTestThread.Run(() =>
    {
        var vm = new LogViewModel();
        Assert.False(vm.HasRun);
        var start = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.FromHours(13));
        vm.BeginRun("1.21.1-Fabric", start);
        Assert.True(vm.HasRun);
        Assert.Equal(L.F("log.run_summary", "1.21.1-Fabric", Fmt.Time(start)), vm.RunSummary);
    });
}
