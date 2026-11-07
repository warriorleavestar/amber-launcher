using Amber.Core.Launch;

namespace Amber.Core.Tests;

public sealed class FreeMemoryTests
{
    [Fact]
    public void ParsesMemAvailable()
    {
        const string sample = "MemTotal:       16305412 kB\nMemFree:          812344 kB\nMemAvailable:    9876543 kB\nBuffers:          123456 kB\nCached:          7000000 kB\n";
        Assert.Equal(9876543, MemoryPlanner.ParseMemAvailableKb(sample));
    }

    [Fact]
    public void OldKernelsWithoutMemAvailableUseFreePlusCaches()
    {
        const string sample = "MemTotal: 2000000 kB\nMemFree: 100000 kB\nBuffers: 20000 kB\nCached: 300000 kB\n";
        Assert.Equal(420000, MemoryPlanner.ParseMemAvailableKb(sample));
        Assert.Null(MemoryPlanner.ParseMemAvailableKb("garbage"));
    }

    [Fact]
    public void LinuxMatchesProcMeminfo()
    {
        if (!OperatingSystem.IsLinux()) return;
        var expected = MemoryPlanner.ParseMemAvailableKb(File.ReadAllText("/proc/meminfo"))!.Value / 1024;
        // 两次读之间内存会变一点，允许 5% 或 256 MB 的差。
        Assert.InRange(MemoryPlanner.FreePhysicalMb(), expected - Math.Max(256, expected / 20), expected + Math.Max(256, expected / 20));
    }

    [Fact]
    public void FreeIsPositiveAndBelowTotal()
    {
        var free = MemoryPlanner.FreePhysicalMb();
        Assert.InRange(free, 1, Math.Max(MemoryPlanner.TotalPhysicalMb(), free));
    }
}
