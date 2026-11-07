using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;

namespace Amber.Core.Tests;

public sealed class PlatformPortingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-platform-" + Guid.NewGuid().ToString("N"));

    public PlatformPortingTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void JayServerAccessKeyIsEncryptedAndRoundTrips()
    {
        var list = new JayServerList(Path.Combine(_root, "servers"));
        var key = RandomNumberGenerator.GetBytes(32);
        var membership = new GateMembership("example.com", 47670, RandomNumberGenerator.GetBytes(32), key, "Steve");
        list.Add(membership);
        var json = File.ReadAllText(Path.Combine(_root, "servers", JayServerList.FileName));
        Assert.DoesNotContain(Convert.ToBase64String(key), json);
        Assert.Contains(OperatingSystem.IsWindows() ? "dpapi:" : "aesgcm:", json);
        Assert.Equal(key, list.ToMembership(list.Load().Single()).AccessKey);
        if (!OperatingSystem.IsWindows())
        {
            var keyPath = Path.Combine(_root, "servers", JayServerList.FileName + ".key");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
        }
    }

    [Fact]
    public void LegacyPlainJayServerKeyMigratesOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "migrate");
        var list = new JayServerList(dir);
        var key = RandomNumberGenerator.GetBytes(32);
        list.Add(new GateMembership("example.com", 47670, RandomNumberGenerator.GetBytes(32), key, "Steve"));
        var path = Path.Combine(dir, JayServerList.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Servers"]!.AsArray()[0]!["ProtectedAccessKey"] = "plain:" + Convert.ToBase64String(key);
        File.WriteAllText(path, json.ToJsonString());

        var entry = list.Load().Single();

        Assert.Contains("aesgcm:", File.ReadAllText(path));
        Assert.Equal(key, list.ToMembership(entry).AccessKey);
    }

    [Fact]
    public void UnixLegacyHomePointsAtTheInstance()
    {
        if (OperatingSystem.IsWindows()) return;
        var instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(instance);
        var home = AsciiPaths.LegacyHome(instance, dataDirectory: Path.Combine(_root, "data"));
        Assert.NotNull(home);
        var link = OperatingSystem.IsMacOS()
            ? Path.Combine(home!, "Library", "Application Support", "minecraft")
            : Path.Combine(home!, ".minecraft");
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.Equal(Path.GetFullPath(instance), Path.GetFullPath(Directory.ResolveLinkTarget(link, true)!.FullName));
    }

    [Fact]
    public void ParsesLinuxAvailableMemory()
    {
        const string sample = "MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:    8192000 kB\n";
        Assert.Equal(8000, MemoryPlanner.ProcMemInfoMb(sample, "MemAvailable"));
        Assert.Equal(16000, MemoryPlanner.ProcMemInfoMb(sample, "MemTotal"));
        Assert.Null(MemoryPlanner.ProcMemInfoMb(sample, "Missing"));
    }

    [Fact]
    public void FullLanCapabilityMatchesTheImplementedPlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PlatformFeatures.FullLan);
        Assert.Equal(OperatingSystem.IsWindows(), PlatformFeatures.FullTrafficExit);
    }

    [Fact]
    public void StaleEasyTierProcessIsCleanedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        var executable = process.MainModule!.FileName!;
        Assert.NotNull(EasyTierProcessTracker.Track(process, _root, executable));

        EasyTierProcessTracker.Cleanup(_root, executable);

        Assert.True(process.WaitForExit(5000));
        Assert.Empty(Directory.EnumerateFiles(_root, ".jay-easytier-*.pid"));
    }

    [Fact]
    public void EasyTierOfAnotherRunningJayIsLeftAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var otherJay = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        using var process = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            var executable = process.MainModule!.FileName!;
            var ownerTicks = otherJay.StartTime.ToUniversalTime().Ticks;
            Assert.NotNull(EasyTierProcessTracker.Track(process, _root, executable, otherJay.Id, ownerTicks));

            EasyTierProcessTracker.Cleanup(_root, executable);
            Assert.False(process.WaitForExit(500));
            Assert.Single(Directory.EnumerateFiles(_root, ".jay-easytier-*.pid"));

            otherJay.Kill();
            otherJay.WaitForExit(5000);
            EasyTierProcessTracker.Cleanup(_root, executable);
            Assert.True(process.WaitForExit(5000));
            Assert.Empty(Directory.EnumerateFiles(_root, ".jay-easytier-*.pid"));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            if (!otherJay.HasExited) otherJay.Kill();
        }
    }

    [Fact]
    public void LinuxExitRequestSendsSigterm()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"trap 'exit 0' TERM; while :; do sleep 1; done\"") { UseShellExecute = false })!;
        Assert.True(GameLauncher.RequestExit(process));
        Assert.True(process.WaitForExit(5000));
    }
}
