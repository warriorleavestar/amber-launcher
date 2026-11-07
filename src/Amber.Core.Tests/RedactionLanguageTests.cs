using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>要切换全局界面语言的测试放在这里，单独跑（别的测试按简体中文断言，并行时会互相影响）。</summary>
[CollectionDefinition("GlobalLanguage", DisableParallelization = true)]
public sealed class GlobalLanguageCollection;

/// <summary>诊断报告的打码在每种界面语言下都一样有效（替换成的占位文字是翻译过的，不能因此漏掉密钥）。</summary>
[Collection("GlobalLanguage")]
public class RedactionLanguageTests
{
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void SecretsAreRemovedInEveryLanguage(string language)
    {
        var before = L.Language;
        try
        {
            L.SetLanguage(language);
            const string secret = "4HZP8RW2N6TC";
            const string configSecret = "0123abcd4567ef";
            var text = $"secret {secret} room AMB-K7Q2M9XD-{secret}-25565 wrapped AMB-T2D-K7Q2M9XD-{secret}-25565 " +
                       $"code AMBDIRECT1-eyJhIjoxfQ reply AMBREPLY1-eyJiIjoyfQ new JAY2D-eyJjIjozfQ jay2r-eyJkIjo0fQ args --network-secret {configSecret} " +
                       $"config network_secret: \"{configSecret}\" other network_secret = Some(\"89abcdef0123\")";
            var redacted = NetDiagnostics.Redact(text, [secret], includePublicIps: false);
            Assert.DoesNotContain(secret, redacted);
            Assert.DoesNotContain(configSecret, redacted);
            Assert.DoesNotContain("89abcdef0123", redacted);
            Assert.DoesNotContain("eyJhIjoxfQ", redacted);
            Assert.DoesNotContain("eyJiIjoyfQ", redacted);
            Assert.DoesNotContain("eyJjIjozfQ", redacted);
            Assert.DoesNotContain("eyJkIjo0fQ", redacted);
            // 网络名和端口留着（排查问题要用）。
            Assert.Contains("K7Q2M9XD", redacted);
            Assert.Contains("25565", redacted);
        }
        finally
        {
            L.SetLanguage(before);
        }
    }
}
