using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>资源页首次进入自动加载；用本地资源源验证，不联网、不读写实际游戏目录。</summary>
public sealed class ContentViewModelTests
{
    [Fact]
    public void FirstActivationLoadsDefaultModsWithoutSearchingAtStartup() => Run((vm, modrinth, curseForge) =>
    {
        Assert.Empty(modrinth.Queries);
        Assert.Empty(curseForge.Queries);
        Assert.Empty(vm.Results);

        vm.Activate();

        Assert.Equal(new SearchQuery(ContentType.Mod, "", null, null), Assert.Single(modrinth.Queries));
        Assert.Equal(ContentSource.Modrinth, Assert.Single(vm.Results).Project.Source);
        Assert.Empty(curseForge.Queries);
    });

    [Fact]
    public void ReturningToPagePreservesResultsPageAndSelection() => Run((vm, modrinth, _) =>
    {
        vm.Activate();
        vm.Query = "sodium";
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.NextPageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var item = Assert.Single(vm.Results);
        vm.Selected = item;
        var requests = modrinth.Queries.Count;

        vm.IsCurrent = false;
        vm.IsCurrent = true;
        vm.Activate();

        Assert.Equal(requests, modrinth.Queries.Count);
        Assert.Equal(1, vm.Page);
        Assert.Equal("sodium", vm.Query);
        Assert.Same(item, Assert.Single(vm.Results));
        Assert.Same(item, vm.Selected);
    });

    [Fact]
    public void SwitchingSourceAutomaticallyLoadsItsFirstPage() => Run((vm, modrinth, curseForge) =>
    {
        vm.Activate();
        vm.Query = "sodium";
        vm.NextPageCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        vm.SourceIndex = 1;

        Assert.Equal(new SearchQuery(ContentType.Mod, "sodium", null, null), Assert.Single(curseForge.Queries));
        Assert.Equal(ContentSource.CurseForge, Assert.Single(vm.Results).Project.Source);
        Assert.Equal(0, vm.Page);
        Assert.Equal(2, modrinth.Queries.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivationDoesNotRepeatSearchAlreadyStartedBeforeNavigation(bool switchSource) => Run((vm, modrinth, curseForge) =>
    {
        vm.Query = "sodium";
        if (switchSource) vm.SourceIndex = 1;
        else vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var item = Assert.Single(vm.Results);

        vm.Activate();

        Assert.Equal(1, modrinth.Queries.Count + curseForge.Queries.Count);
        Assert.Equal("sodium", vm.Query);
        Assert.Same(item, Assert.Single(vm.Results));
    });

    [Fact]
    public void AutomaticSearchReportsFailureAndManualSearchCanRetry() => Run((vm, _, curseForge) =>
    {
        vm.Activate();
        curseForge.Failure = new MissingApiKeyException();

        vm.SourceIndex = 1;

        Assert.Empty(vm.Results);
        Assert.Equal(StatusKind.Error, vm.StatusInfo?.Kind);
        Assert.Equal("content.search.failed", vm.StatusInfo?.Key);
        Assert.Contains(nameof(MissingApiKeyException), vm.StatusInfo?.Detail);

        curseForge.Failure = null;
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Equal(2, curseForge.Queries.Count);
        Assert.Equal(ContentSource.CurseForge, Assert.Single(vm.Results).Project.Source);
        Assert.NotEqual(StatusKind.Error, vm.StatusInfo?.Kind);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateResponseFromCancelledInitialSearchDoesNotReplaceNewSourceResults(bool fail) => Run((vm, modrinth, curseForge) =>
    {
        var pending = new TaskCompletionSource<SearchResult>();
        modrinth.PendingSearch = pending.Task;
        vm.Activate();
        Assert.Empty(vm.Results);

        vm.SourceIndex = 1;
        var item = Assert.Single(vm.Results);
        var status = vm.StatusInfo;
        Assert.True(Assert.Single(modrinth.Tokens).IsCancellationRequested);

        if (fail) pending.SetException(new HttpRequestException("已取消的资源源请求随后失败"));
        else pending.SetResult(new SearchResult([], 0));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(curseForge.Queries);
        Assert.Equal(ContentSource.CurseForge, item.Project.Source);
        Assert.Same(item, Assert.Single(vm.Results));
        Assert.Same(status, vm.StatusInfo);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportCancelsAutomaticSearchAndKeepsItsOwnStatus(bool fail) => Run((vm, modrinth, _) =>
    {
        var pending = new TaskCompletionSource<SearchResult>();
        modrinth.PendingSearch = pending.Task;
        vm.Activate();
        var missingPack = Path.Combine(Path.GetTempPath(), "amber-missing-pack-" + Guid.NewGuid().ToString("N") + ".zip");

        vm.ImportLocalPackAsync(missingPack).GetAwaiter().GetResult();
        var status = vm.StatusInfo;
        Assert.Equal("instance.worlds.import_failed", status?.Key);
        Assert.True(Assert.Single(modrinth.Tokens).IsCancellationRequested);

        if (fail) pending.SetException(new HttpRequestException("已取消的资源源请求随后失败"));
        else pending.SetResult(new SearchResult([], 0));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(status, vm.StatusInfo);
        Assert.Empty(vm.Results);
    });

    private static void Run(Action<ContentViewModel, FakeProvider, FakeProvider> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-content-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var modrinth = new FakeProvider(ContentSource.Modrinth);
                var curseForge = new FakeProvider(ContentSource.CurseForge);
                var vm = new ContentViewModel(service, index => index == 0 ? modrinth : curseForge);
                test(vm, modrinth, curseForge);
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeProvider(ContentSource source) : IContentProvider
    {
        public ContentSource Source => source;
        public List<SearchQuery> Queries { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Exception? Failure { get; set; }
        public Task<SearchResult>? PendingSearch { get; set; }

        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            Tokens.Add(ct);
            if (PendingSearch is { } pending) return pending;
            if (Failure is { } failure) return Task.FromException<SearchResult>(failure);
            var project = new ContentProject(Source, $"{Source}-{query.Offset}", "测试模组", "", null, null, 1, null);
            return Task.FromResult(new SearchResult([project], 40));
        }

        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ContentFile>>([]);

        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
