using Amber.App.ViewModels;
using Amber.Core.Install;

namespace Amber.Core.Tests;

/// <summary>新建实例的默认名字（改版规格 4.4）：“{MC 版本}-{加载器}”，重名加序号。</summary>
public sealed class NewInstanceNameTests
{
    [Fact]
    public void DefaultNameIsVersionAndLoader() =>
        Assert.Equal("1.21.1-Fabric", DownloadViewModel.DefaultName("1.21.1", LoaderKind.Fabric, []));

    [Fact]
    public void DuplicateNamesGetNextFreeNumber()
    {
        Assert.Equal("1.21.1-Fabric-2", DownloadViewModel.DefaultName("1.21.1", LoaderKind.Fabric, ["1.21.1-Fabric"]));
        Assert.Equal("1.20.1-Forge-4", DownloadViewModel.DefaultName("1.20.1", LoaderKind.Forge, ["1.20.1-forge", "1.20.1-Forge-2", "1.20.1-Forge-3"]));
    }

    [Fact]
    public void OtherLoadersDoNotTakeTheName() =>
        Assert.Equal("1.21.1-NeoForge", DownloadViewModel.DefaultName("1.21.1", LoaderKind.NeoForge, ["1.21.1-Fabric", "1.21.1"]));
}
