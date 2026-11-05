using Amber.Core.Launch;

namespace Amber.Core.Tests;

public sealed class LogLevelParserTests
{
    [Theory]
    [InlineData("[12:34:56] [Render thread/WARN]: Missing texture", GameLogLevel.Warn)]
    [InlineData("[main/ERROR] Failed to load", GameLogLevel.Error)]
    [InlineData("[16:00:00 INFO] Starting server", GameLogLevel.Info)]
    [InlineData("[16:00:00 WARN]: Can't keep up!", GameLogLevel.Warn)]
    [InlineData("[16:00:00.123] [main/DEBUG] Resolving assets", GameLogLevel.Debug)]
    [InlineData("[16:00:00,123] [main/TRACE] Asset details", GameLogLevel.Trace)]
    [InlineData("[12:34:56] [Server thread/FATAL] [minecraft/Server]: Stopped", GameLogLevel.Fatal)]
    [InlineData("  [main/warn]: Resource warning", GameLogLevel.Warn)]
    [InlineData("[ERROR] Failed to initialize", GameLogLevel.Error)]
    public void RecognizesLeadingLogHeaders(string message, GameLogLevel expected) =>
        Assert.Equal(expected, LogLevelParser.Parse(message));

    [Theory]
    [InlineData("Exception in thread \"main\" java.lang.RuntimeException: failed")]
    [InlineData("\tat net.minecraft.client.main.Main.main(Main.java:42)")]
    [InlineData("    at net.minecraft.client.main.Main.main(Main.java:42)")]
    [InlineData("Caused by: java.io.IOException: missing file")]
    [InlineData("\tCaused by: java.lang.IllegalStateException: failed")]
    [InlineData("\tSuppressed: java.io.IOException: failed to close")]
    public void RecognizesJavaStackTraceLines(string message) =>
        Assert.Equal(GameLogLevel.Error, LogLevelParser.Parse(message));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Loading ERROR textures")]
    [InlineData("No Exception was raised")]
    [InlineData("Exception handlers installed")]
    [InlineData("at the spawn point")]
    [InlineData("Message contains [main/ERROR] as an example")]
    [InlineData("[ERROR_COUNT] 0")]
    [InlineData("[arbitrary ERROR] example")]
    [InlineData("[12:34:56] [main/UNKNOWN]: [ERROR] example")]
    [InlineData("[main/ERROR")]
    [InlineData("[00:00:xx INFO] example")]
    public void UnmarkedOrMalformedMessagesRemainInfo(string message) =>
        Assert.Equal(GameLogLevel.Info, LogLevelParser.Parse(message));

    [Theory]
    [InlineData(GameLogLevel.Trace)]
    [InlineData(GameLogLevel.Debug)]
    [InlineData(GameLogLevel.Warn)]
    [InlineData(GameLogLevel.Error)]
    [InlineData(GameLogLevel.Fatal)]
    public void PreservesStructuredFallbackForUnmarkedMessages(GameLogLevel fallback) =>
        Assert.Equal(fallback, LogLevelParser.Parse("Loading assets", fallback));

    [Fact]
    public void ExplicitHeaderTakesPrecedenceOverFallbackAndMessageWords()
    {
        Assert.Equal(GameLogLevel.Warn, LogLevelParser.Parse("[main/WARN]: ERROR while loading", GameLogLevel.Info));
        Assert.Equal(GameLogLevel.Info, LogLevelParser.Parse("[main/INFO]: no Exception was raised", GameLogLevel.Error));
        Assert.Equal(GameLogLevel.Error, LogLevelParser.Parse("Caused by: java.io.IOException", GameLogLevel.Debug));
    }
}
