using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>实例页的 Tab（改版规格 4.3）：默认打开哪个、每个实例记住上次的 Tab。</summary>
public sealed class InstanceTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-instance-tab-" + Guid.NewGuid().ToString("N"));

    public InstanceTabTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void TabOrderPutsModsFirstAndSettingsLast()
    {
        Assert.Equal(
            [InstanceTab.Mods, InstanceTab.Worlds, InstanceTab.ResourcePacks, InstanceTab.ShaderPacks, InstanceTab.Servers, InstanceTab.Screenshots, InstanceTab.Settings],
            Enum.GetValues<InstanceTab>());
        Assert.Equal(0, (int)InstanceTab.Mods);
    }

    [Fact]
    public void DefaultTabIsModsWithLoaderAndWorldsForVanilla()
    {
        Assert.Equal(InstanceTab.Mods, InstanceViewModel.DefaultTab(new GameInstance("f", "1.21.1", "fabric", "0.16", _root)));
        Assert.Equal(InstanceTab.Worlds, InstanceViewModel.DefaultTab(new GameInstance("v", "1.21.1", null, null, _root)));
    }

    /// <summary>保存实例设置后按新设置重新读实例：改了隔离，工作目录跟着变（不用重新选一次实例）。</summary>
    [Fact]
    public void SavingIsolationRefreshesWorkDir() => UiTestThread.Run(() =>
    {
        var dir = Path.Combine(_root, "versions", "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"), """{"id":"a","mainClass":"Example","libraries":[]}""");
        File.WriteAllBytes(Path.Combine(dir, "a.jar"), []);
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var vm = new InstanceViewModel(service);
        vm.SelectedId = "a";
        vm.IsolateIndex = 2; // 不隔离
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(vm.Current!.WorkDir));
        vm.IsolateIndex = 1; // 隔离
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(vm.Current!.WorkDir));
    });

    [Fact]
    public void EachInstanceRemembersItsLastTab() => UiTestThread.Run(() =>
    {
        foreach (var id in new[] { "a", "b" })
        {
            var dir = Path.Combine(_root, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), $$"""{"id":"{{id}}","mainClass":"Example","libraries":[]}""");
            File.WriteAllBytes(Path.Combine(dir, id + ".jar"), []);
        }
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var vm = new InstanceViewModel(service);

        vm.SelectedId = "a";
        Assert.Equal((int)InstanceTab.Worlds, vm.TabIndex); // 原版默认看世界
        vm.TabIndex = (int)InstanceTab.ShaderPacks;

        vm.SelectedId = "b";
        Assert.Equal((int)InstanceTab.Worlds, vm.TabIndex);
        vm.TabIndex = (int)InstanceTab.Settings;

        vm.SelectedId = "a";
        Assert.Equal((int)InstanceTab.ShaderPacks, vm.TabIndex);
        vm.SelectedId = "b";
        Assert.Equal((int)InstanceTab.Settings, vm.TabIndex);
    });
}
