using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>jay-server 的参数解析：--help 放在哪里都显示用法，不会因为“后面没有值”而崩。</summary>
public sealed class JayServerCliTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("/?")]
    [InlineData("invite", "--help")]
    [InlineData("run", "-h")]
    public void HelpFlagsAnywhereShowUsage(params string[] args)
    {
        var options = Options.Parse(args);
        Assert.True(options.WantsHelp);
    }

    [Fact]
    public void HelpAsUsernameIsNotHelp()
    {
        var options = Options.Parse(["invite", "help"]);
        Assert.False(options.WantsHelp);
        Assert.Equal(["help"], options.Positional);
    }

    [Fact]
    public void MissingValueIsAnArgumentError()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["invite", "Steve", "--ttl"]));
    }

    [Fact]
    public async Task MainReturnsErrorCodeInsteadOfCrashing()
    {
        Assert.Equal(1, await Program.Main(["invite", "Steve", "--ttl"]));
        Assert.Equal(0, await Program.Main(["--help"]));
    }
}
