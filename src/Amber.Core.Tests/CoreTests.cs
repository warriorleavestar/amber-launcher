using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public class CoreTests
{
    [Fact]
    public void OfflineUuid_MatchesJavaNameUuidFromBytes()
    {
        // Java: UUID.nameUUIDFromBytes("OfflinePlayer:Notch".getBytes(UTF_8))
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", OfflineAccount.OfflineUuid("Notch").ToString());
    }

    [Theory]
    [InlineData("1.8.0_401", 8)]
    [InlineData("17.0.10", 17)]
    [InlineData("21", 21)]
    [InlineData("25.0.3+9", 25)]
    public void ParseJavaMajor(string version, int expected) => Assert.Equal(expected, JavaFinder.ParseMajor(version));

    [Fact]
    public void MavenPath_WithClassifierAndExtension()
    {
        var c = MavenCoordinate.Parse("org.lwjgl:lwjgl:3.3.3:natives-windows");
        Assert.Equal("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar", c.RelativePath);
        Assert.Equal("de/oceanlabs/mcp/mcp_config/1.20.1/mcp_config-1.20.1.zip",
            MavenCoordinate.Parse("de.oceanlabs.mcp:mcp_config:1.20.1@zip").RelativePath);
    }

    [Fact]
    public void Rules_LastMatchWins()
    {
        var eval = new RuleEvaluator { OsName = "osx", Arch = "x86_64" };
        List<Rule> rules = [new() { Action = "allow" }, new() { Action = "disallow", Os = new() { Name = "osx" } }];
        Assert.False(eval.IsAllowed(rules));
        Assert.True(new RuleEvaluator { OsName = "windows", Arch = "x86_64" }.IsAllowed(rules));
        Assert.False(eval.IsAllowed([new() { Action = "allow", Features = new() { ["is_demo_user"] = true } }]));
    }

    [Fact]
    public void Bmclapi_RewritesKnownHosts()
    {
        var chain = SourceChain.MirrorFirst;
        var c = chain.Candidates("https://resources.download.minecraft.net/ab/abcdef");
        Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef", c[0]);
        Assert.Equal("https://resources.download.minecraft.net/ab/abcdef", c[^1]);
        // 镜像不认识的地址只保留官方
        Assert.Single(chain.Candidates("https://example.com/x.jar"));
    }

    [Fact]
    public void ResolveVersion_MergesInheritsFrom()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = new GameDirectory(root);
            Write(dir, "1.21", """
                {"id":"1.21","mainClass":"net.minecraft.client.main.Main","assets":"17",
                 "libraries":[{"name":"org.ow2.asm:asm:9.6"},{"name":"com.google.guava:guava:32.1.2-jre"}],
                 "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]}}
                """);
            Write(dir, "fabric-1.21", """
                {"id":"fabric-1.21","inheritsFrom":"1.21","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
                 "libraries":[{"name":"org.ow2.asm:asm:9.7","url":"https://maven.fabricmc.net/"}],
                 "arguments":{"game":[],"jvm":["-DFabricMcEmu= net.minecraft.client.main.Main "]}}
                """);

            var v = dir.ResolveVersion("fabric-1.21");
            Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", v.MainClass);
            Assert.Equal("1.21", v.Jar);
            Assert.Equal(["org.ow2.asm:asm:9.7", "com.google.guava:guava:32.1.2-jre"], v.Libraries.Select(l => l.Name));
            Assert.Equal(3, v.Arguments!.Jvm.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }

        static void Write(GameDirectory dir, string id, string json)
        {
            Directory.CreateDirectory(dir.VersionDir(id));
            File.WriteAllText(dir.VersionJsonPath(id), json);
        }
    }
}
