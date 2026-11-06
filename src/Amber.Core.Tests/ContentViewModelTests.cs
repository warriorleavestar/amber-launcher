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
    public void PagingKeepsTheDetailPanelOpen() => Run((vm, modrinth, _) =>
    {
        // #22：点“下一页”时右侧详情不收起，鼠标不用挪就能接着翻。
        vm.Activate();
        var item = vm.Results[0];
        vm.Selected = item;
        Assert.True(vm.HasSelection);

        vm.NextPageCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Equal(1, vm.Page);
        Assert.Equal(20, modrinth.Queries[^1].Offset);
        Assert.Same(item, vm.Detail);
        Assert.True(vm.HasSelection);
        Assert.NotSame(item, Assert.Single(vm.Results));

        vm.PreviousPageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(0, vm.Page);
        Assert.Same(item, vm.Detail);

        vm.Query = "其他";
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.False(vm.HasSelection);
    });

    [Fact]
    public void OptionalDependenciesIncludePinnedFilesAndAreListedOnce()
    {
        var file = new ContentFile(ContentSource.Modrinth, "A", "a1", "a", "a.jar", "https://x/a", null, 1, ["1.21.1"], ["fabric"], "release", default,
        [
            new(ContentSource.Modrinth, "API", null, DependencyKind.Required),
            new(ContentSource.Modrinth, "OPAC", null, DependencyKind.Optional),
            new(ContentSource.Modrinth, "OPAC", "f2", DependencyKind.Optional),
            new(ContentSource.Modrinth, null, "f3", DependencyKind.Optional),
            new(ContentSource.Modrinth, null, "f3", DependencyKind.Optional),
            new(ContentSource.Modrinth, null, null, DependencyKind.Optional),
        ]);
        Assert.Equal([("OPAC", (string?)null), (null, "f3")], ContentViewModel.OptionalDependencyIds(file).Select(d => (d.ProjectId, d.FileId)));
    }

    [Fact]
    public void PinnedOptionalDependencyKeepsItsCheckboxWhenNameLookupFails() => Run((vm, modrinth, _) =>
    {
        var optional = TestFile("OPAC", "opac1");
        modrinth.FileById = optional;
        vm.SelectedFile = new FileItem(TestFile("A", "a1", new ContentDependency(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)));

        var item = Assert.Single(vm.OptionalDependencies);
        Assert.Null(item.ProjectId);
        Assert.Equal("opac1", item.FileId);
        Assert.Equal(optional.DisplayName, item.Name);
        Assert.False(item.Include);
        item.Include = true;
        Assert.True(item.Include);
        Assert.True(vm.HasOptionalDependencies);

        vm.SelectedFile = null;
        Assert.Empty(vm.OptionalDependencies);
        Assert.False(vm.HasOptionalDependencies);
    });

    [Fact]
    public void ChangingFileCancelsPendingOptionalNameLookup() => Run((vm, modrinth, _) =>
    {
        var pending = new TaskCompletionSource<ContentFile>();
        modrinth.PendingFile = pending.Task;
        vm.SelectedFile = new FileItem(TestFile("A", "a1", new ContentDependency(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)));
        var oldItem = Assert.Single(vm.OptionalDependencies);

        vm.SelectedFile = new FileItem(TestFile("B", "b1"));
        Assert.True(modrinth.FileToken.IsCancellationRequested);
        pending.SetResult(TestFile("OPAC", "opac1"));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.OptionalDependencies);
        Assert.Equal("opac1", oldItem.Name);
    });

    [Fact]
    public void FailedPinnedOptionalFileLookupStillLeavesAnUnselectedChoice() => Run((vm, modrinth, _) =>
    {
        modrinth.PendingFile = Task.FromException<ContentFile>(new InvalidDataException("empty version metadata"));
        vm.SelectedFile = new FileItem(TestFile("A", "a1", new ContentDependency(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)));

        var item = Assert.Single(vm.OptionalDependencies);
        Assert.Equal("opac1", item.Name);
        Assert.False(item.Include);
        Assert.True(vm.HasOptionalDependencies);
    });

    [Fact]
    public void ChangingFileUpdatesManualDownloadAndOptionalDependenciesTogether() => Run((vm, modrinth, _) =>
    {
        modrinth.FileById = TestFile("OPAC", "opac1");
        var restricted = TestFile("A", "a1", new ContentDependency(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)) with
        {
            Source = ContentSource.CurseForge,
            Url = "",
            ManualDownloadPage = "https://www.curseforge.com/projects/123",
        };

        vm.SelectedFile = new FileItem(restricted);
        Assert.Equal(restricted.ManualDownloadPage, vm.ManualDownloadUrl);
        Assert.True(vm.SelectedRequiresManualDownload);
        Assert.Equal("opac1", Assert.Single(vm.OptionalDependencies).FileId);

        vm.SelectedFile = new FileItem(TestFile("B", "b1"));
        Assert.False(vm.HasManualDownload);
        Assert.False(vm.SelectedRequiresManualDownload);
        Assert.Empty(vm.OptionalDependencies);
        Assert.True(vm.InstallCommand.CanExecute(null));

        vm.SelectedFile = null;
        Assert.False(vm.HasManualDownload);
        Assert.False(vm.InstallCommand.CanExecute(null));
    });

    [Fact]
    public void RestrictedDependencyHintStillAllowsRetryAfterManualDownload() => Run((vm, _, _) =>
    {
        vm.SelectedFile = new FileItem(TestFile("A", "a1"));
        vm.ManualDownloadUrl = "https://www.curseforge.com/projects/123";

        Assert.True(vm.HasManualDownload);
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.IsBusy = false;
        Assert.True(vm.InstallCommand.CanExecute(null));

        vm.SelectedFile = null;
        Assert.False(vm.HasManualDownload);
        Assert.False(vm.InstallCommand.CanExecute(null));
    });

    private static ContentFile TestFile(string project, string id, params ContentDependency[] dependencies) =>
        new(ContentSource.Modrinth, project, id, "name-" + id, id + ".jar", "https://x/" + id, null, 1,
            ["1.21.1"], ["fabric"], "release", default, dependencies);

    [Fact]
    public void CurseForgeWithoutKeyIsGreyedAndNeverQueried() => Run((vm, _, curseForge) =>
    {
        vm.Activate();
        Assert.False(vm.SourceOptions[1].Enabled);

        vm.SourceIndex = 1;

        Assert.Empty(curseForge.Queries);
        Assert.True(vm.NeedsCurseForgeKey);
        Assert.False(vm.IsSearching);
        Assert.Equal("core.content.curseforge.missingapikey", vm.StatusInfo?.Key);

        vm.UseModrinthCommand.Execute(null);
        Assert.Equal(0, vm.SourceIndex);
        Assert.False(vm.NeedsCurseForgeKey);
    }, hasCurseForge: false);

    [Fact]
    public void EmptyAndFailedSearchesShowTheirOwnState() => Run((vm, modrinth, _) =>
    {
        vm.Activate();
        Assert.False(vm.IsEmptyResult);

        modrinth.Empty = true;
        vm.Query = "不存在的模组";
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(vm.IsEmptyResult);
        Assert.Contains("不存在的模组", vm.EmptyText);
        Assert.False(vm.IsSearching);

        modrinth.Empty = false;
        modrinth.Failure = new HttpRequestException("boom");
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(vm.IsSearchError);
        Assert.False(vm.IsEmptyResult);

        modrinth.Failure = null;
        vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.False(vm.IsSearchError);
        Assert.Single(vm.Results);
        Assert.False(vm.IsIdle);
    });

    [Fact]
    public void ShowAllVersionsTurnsOffInstanceFilterAndSearchesAgain() => Run((vm, modrinth, _) =>
    {
        vm.Activate();
        var before = modrinth.Queries.Count;
        Assert.True(vm.FilterByInstance);

        vm.ShowAllVersionsCommand.Execute(null);

        Assert.False(vm.FilterByInstance);
        Assert.Equal(before + 1, modrinth.Queries.Count);
    });

    [Fact]
    public void InstallIsDisabledUntilAFileIsPicked() => Run((vm, _, _) =>
    {
        vm.Activate();
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal(L.T("content.install.pick_file"), vm.InstallHint);
    });

    [Fact]
    public void ModsAreBlockedOnlyForVanillaInstances()
    {
        var vanilla = new GameInstance("v", "1.21.1", null, null, "x");
        var fabric = vanilla with { Loader = "fabric" };
        Assert.True(ContentViewModel.BlocksVanillaModInstall(ContentType.Mod, vanilla));
        Assert.False(ContentViewModel.BlocksVanillaModInstall(ContentType.Mod, fabric));
        Assert.False(ContentViewModel.BlocksVanillaModInstall(ContentType.ResourcePack, vanilla));
        Assert.False(ContentViewModel.BlocksVanillaModInstall(ContentType.Shader, vanilla));
    }

    [Fact]
    public void SlowHintMentionsModrinthAndWaitedSeconds()
    {
        Assert.Contains("12", ContentViewModel.SlowMessage(0, 12));
        Assert.Contains("Modrinth", ContentViewModel.SlowMessage(0, 12));
        Assert.Contains("CurseForge", ContentViewModel.SlowMessage(1, 12));
        Assert.Equal(TimeSpan.FromSeconds(10), ContentViewModel.SlowAfter);
    }

    [Fact]
    public void BrowseForFromInstanceShowsContextAndReturnClearsIt() => Run((vm, modrinth, _) =>
    {
        vm.Activate();
        var returned = 0;

        vm.BrowseFor("missing-instance", ContentType.Shader, back: () => returned++);

        Assert.Equal((int)ContentType.Shader, vm.TypeIndex);
        Assert.True(vm.HasReturn);
        Assert.Contains("missing-instance", vm.ReturnContext);
        Assert.Equal(ContentType.Shader, modrinth.Queries[^1].Type);

        vm.ReturnCommand.Execute(null);

        Assert.Equal(1, returned);
        Assert.False(vm.HasReturn);
        Assert.Null(vm.ReturnContext);
    });

    [Fact]
    public void BrowseForWithoutBackHidesContextBar() => Run((vm, _, _) =>
    {
        vm.Activate();
        vm.BrowseFor("x", ContentType.ResourcePack, back: () => { });
        vm.BrowseModsFor("x");

        Assert.False(vm.HasReturn);
        Assert.Equal((int)ContentType.Mod, vm.TypeIndex);
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

        Assert.True(vm.IsSearching);
        vm.ImportLocalPackAsync(missingPack).GetAwaiter().GetResult();
        var status = vm.StatusInfo;
        Assert.Equal("instance.worlds.import_failed", status?.Key);
        Assert.True(Assert.Single(modrinth.Tokens).IsCancellationRequested);
        // 被导入取消的搜索不能留着“正在搜索”的转圈。
        Assert.False(vm.IsSearching);

        if (fail) pending.SetException(new HttpRequestException("已取消的资源源请求随后失败"));
        else pending.SetResult(new SearchResult([], 0));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(status, vm.StatusInfo);
        Assert.Empty(vm.Results);
    });

    private static void Run(Action<ContentViewModel, FakeProvider, FakeProvider> test) => Run(test, hasCurseForge: true);

    private static void Run(Action<ContentViewModel, FakeProvider, FakeProvider> test, bool hasCurseForge)
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
                var vm = new ContentViewModel(service, index => index == 0 ? modrinth : curseForge, () => hasCurseForge);
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
        public bool Empty { get; set; }
        public ContentFile? FileById { get; set; }
        public Task<ContentFile>? PendingFile { get; set; }
        public CancellationToken FileToken { get; private set; }

        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            Tokens.Add(ct);
            if (PendingSearch is { } pending) return pending;
            if (Failure is { } failure) return Task.FromException<SearchResult>(failure);
            if (Empty) return Task.FromResult(new SearchResult([], 0));
            var project = new ContentProject(Source, $"{Source}-{query.Offset}", "测试模组", "", null, null, 1, null);
            return Task.FromResult(new SearchResult([project], 40));
        }

        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ContentFile>>([]);

        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default)
        {
            FileToken = ct;
            return PendingFile ?? (FileById is { } file ? Task.FromResult(file) : throw new NotSupportedException());
        }
    }
}
