using Amber.Core.Multiplayer.Gate;
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

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0m", 0)]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("3d", 4320)]
    public void DurationsIncludingBareZero(string text, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), Amber.Server.Program.ParseDuration(text));

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

    [Fact]
    public void RelayAndPrivacyCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Assert.Throws<ArgumentException>(() => Run("privacy", "plus"));
            Run("relays", "add", "198.51.100.7:47671");
            Run("relays", "add", "203.0.113.10:47672", "--source", "127.0.0.1");
            Assert.Throws<ArgumentException>(() => Run("relays", "add", "no-port"));
            Assert.Equal(0, Run("privacy", "plus"));
            Run("relays", "list");
            Assert.Contains("127.0.0.1", output.ToString());
            var state = new GateStore(Path.Combine(root, "gate")).Read();
            Assert.Equal(PrivacyLevel.Plus, state.Privacy);
            Assert.Equal(2, state.Relays.Count);
            output.GetStringBuilder().Clear();
            Run("invite", "Steve");
            var invite = GateInvite.TryParse(output.ToString(), out _)!;
            Assert.Contains($"{invite.Host}:{invite.Port}", new[] { "198.51.100.7:47671", "203.0.113.10:47672" });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void PlusPlusCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Run("relays", "add", "198.51.100.7:47671");
            Assert.NotNull(GateRelayCode.TryParse(output.ToString()));
            Run("relays", "add", "198.51.100.8:47671");
            Assert.Equal(0, Run("privacy", "plusplus"));
            output.GetStringBuilder().Clear();
            Run("relays", "code", "198.51.100.8:47671");
            Assert.NotNull(GateRelayCode.TryParse(output.ToString()));
            Assert.Equal(0, Run("status"));
            Assert.Contains("隐私++", output.ToString());
            Run("relays", "revoke", "198.51.100.8:47671");
            var state = new GateStore(Path.Combine(root, "gate")).Read();
            Assert.Equal(1, state.Epoch);
            Assert.Single(state.Relays);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RotateCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Assert.Equal(0, Run("rotate", "on", "--window", "5m", "--range", "30000-40000", "--grace", "1d"));
            var store = new GateStore(Path.Combine(root, "gate"));
            Assert.Equal(new GateRotation(300, 30000, 40000), store.Rotation()!.Value.Rule);
            Assert.NotNull(store.Read().Rotation!.FixedUntil);
            Run("rotate", "status");
            Assert.Contains("网关直连", output.ToString());
            Run("invite", "Steve");
            Assert.NotNull(GateInvite.TryParse(output.ToString(), out _)!.Rotation);
            Assert.Throws<ArgumentException>(() => Run("rotate", "on", "--range", "30000-30010"));
            Run("rotate", "off");
            Assert.Null(store.Rotation());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
