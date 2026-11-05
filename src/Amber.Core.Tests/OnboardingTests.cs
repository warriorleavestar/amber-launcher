using Amber.Core.Auth;
using Amber.Core.Game;

namespace Amber.Core.Tests;

/// <summary>新手卡用到的 Core 部分：离线名规则、找已有游戏目录（只在临时目录里找）。</summary>
public sealed class OnboardingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-onboarding-test-" + Guid.NewGuid().ToString("N")[..8]);

    public OnboardingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Steve", OfflineNameProblem.None, false)]
    [InlineData("a_b", OfflineNameProblem.None, false)]
    [InlineData("1234567890123456", OfflineNameProblem.None, false)]
    [InlineData("", OfflineNameProblem.Empty, false)]
    [InlineData("ab", OfflineNameProblem.TooShort, false)]
    [InlineData("12345678901234567", OfflineNameProblem.TooLong, false)]
    [InlineData("Ste ve", OfflineNameProblem.HasSpace, false)]
    [InlineData(" Steve", OfflineNameProblem.HasSpace, false)]
    [InlineData("玩家小明", OfflineNameProblem.None, true)]
    [InlineData("Steve-1", OfflineNameProblem.None, true)]
    [InlineData("小明", OfflineNameProblem.TooShort, true)]
    public void OfflineNameRulesSplitHardRulesFromWarnings(string name, OfflineNameProblem problem, bool notAscii)
    {
        var check = OfflineNameRules.Check(name);
        Assert.Equal(problem, check.Problem);
        Assert.Equal(notAscii, check.NotAscii);
        Assert.Equal(problem == OfflineNameProblem.None, check.Ok);
    }

    /// <summary>按看到的字符数算：emoji 这种两个 UTF-16 单元的字符算一个。</summary>
    [Fact]
    public void OfflineNameLengthCountsTextElements()
    {
        Assert.True(OfflineNameRules.Check("ab😀").Ok);
        Assert.Equal(OfflineNameProblem.TooLong, OfflineNameRules.Check(new string('好', 17)).Problem);
    }

    private string MakeGameDir(string relative, params string[] versions)
    {
        var dir = Path.Combine(_root, relative);
        foreach (var v in versions)
        {
            Directory.CreateDirectory(Path.Combine(dir, "versions", v));
            File.WriteAllText(Path.Combine(dir, "versions", v, v + ".json"), "{}");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void DetectorFindsDirectoriesWithInstancesMostFirst()
    {
        var one = MakeGameDir(@"HMCL\.minecraft", "1.20.1");
        var three = MakeGameDir(@"PCL\.minecraft", "1.20.1", "1.12.2", "fabric");
        var empty = MakeGameDir(@"Empty\.minecraft");
        Directory.CreateDirectory(Path.Combine(_root, @"NoJson\.minecraft\versions\1.8"));

        var found = GameDirectoryDetector.Find([one, three, empty, Path.Combine(_root, @"NoJson\.minecraft"), Path.Combine(_root, "missing")]);
        Assert.Equal([three, one], found.Select(f => f.Path));
        Assert.Equal([3, 1], found.Select(f => f.InstanceCount));
    }

    [Fact]
    public void DetectorSkipsTheCurrentDirectoryAndDuplicates()
    {
        var current = MakeGameDir("current", "1.20.1");
        var other = MakeGameDir("other", "1.20.1");
        var found = GameDirectoryDetector.Find([current, other, other + Path.DirectorySeparatorChar, Path.Combine(_root, "x", "..", "other")], exclude: current + Path.DirectorySeparatorChar);
        Assert.Equal([other], found.Select(f => f.Path));
    }

    [Fact]
    public void DetectorRespectsLimitAndIgnoresBadPaths()
    {
        var dirs = Enumerable.Range(0, 4).Select(i => MakeGameDir("g" + i, "1.20.1")).ToList();
        var found = GameDirectoryDetector.Find([.. dirs, "\0bad"], limit: 2);
        Assert.Equal(2, found.Count);
        Assert.Equal(dirs.Take(2), found.Select(f => f.Path));
    }

    /// <summary>常见位置的第一个是默认 .minecraft；启动器旁边的 .minecraft 也在里面。只生成路径，不读写。</summary>
    [Fact]
    public void CommonCandidatesStartWithTheDefaultRoot()
    {
        var launcher = Path.Combine(_root, "launcher");
        var list = GameDirectoryDetector.CommonCandidates(launcher).Take(2).ToList();
        Assert.Equal(GameDirectory.DefaultRoot(), list[0]);
        Assert.Equal(Path.Combine(launcher, ".minecraft"), list[1]);
    }
}
