using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>联机页的计算属性要在每个依赖变化时通知界面，否则按钮要切页面才出现。</summary>
public sealed class MultiplayerVisibilityTests
{
    [Fact]
    public void DirectHostControlsAppearWhenModeChangesAfterDirectSessionAttaches() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsDirect = true;
        Assert.False(vm.IsDirectHost);
        changed.Clear();

        vm.Mode = "host";
        Assert.True(vm.IsDirectHost);
        Assert.Contains(nameof(MultiplayerViewModel.IsDirectHost), changed);

        changed.Clear();
        vm.Mode = "";
        Assert.False(vm.IsDirectHost);
        Assert.Contains(nameof(MultiplayerViewModel.IsDirectHost), changed);
    });

    [Fact]
    public void PageShowsDirectControlsWithoutReopening() => UiTestThread.Run(() =>
    {
        EnsureAppResources();
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var page = new MultiplayerPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1000, Height = 800 };
        window.Show();
        try
        {
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            bool Shown(string key) => page.GetLogicalDescendants().OfType<Button>()
                .Any(b => Equals(b.Content, L.T(key)) && b.IsEffectivelyVisible);
            Layout();
            Assert.False(Shown("multiplayer.direct.connect"));

            vm.IsDirect = true;
            Layout();
            vm.Mode = "host";
            Layout();
            Assert.True(Shown("multiplayer.direct.connect"));

            vm.Mode = "join";
            vm.TestRequestPending = true;
            Layout();
            Assert.False(Shown("multiplayer.direct.connect"));
            Assert.True(Shown("multiplayer.matrix.accept"));
        }
        finally { window.Close(); }
    });

    private static void EnsureAppResources()
    {
        var resources = Application.Current!.Resources;
        if (resources.ContainsKey("RadiusMd")) return;
        resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Jay/"))
        {
            Source = new Uri("avares://Jay/Themes/Tokens.axaml"),
        });
    }
}
