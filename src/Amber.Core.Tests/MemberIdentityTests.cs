using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>联机身份：保存后读回是同一把；文件坏了换新的并提示、不崩；指纹格式；重置。</summary>
public sealed class MemberIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-identity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTrips()
    {
        byte[] first;
        using (var identity = MemberIdentity.LoadOrCreate(_dir))
        {
            Assert.Null(identity.Warning);
            first = identity.PublicKey;
        }
        Assert.True(File.Exists(Path.Combine(_dir, MemberIdentity.FileName)));
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Null(again.Warning);
        Assert.Equal(first, again.PublicKey);
        // 文件里不是明文私钥。
        var raw = again.KeyPair.ExportPrivateKey();
        var stored = File.ReadAllBytes(Path.Combine(_dir, MemberIdentity.FileName));
        Assert.False(stored.AsSpan().IndexOf(raw) >= 0);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("AMBID2short")]
    public void CorruptFileGivesNewIdentityWithWarning(string content)
    {
        byte[] old;
        using (var identity = MemberIdentity.LoadOrCreate(_dir)) old = identity.PublicKey;
        var path = Path.Combine(_dir, MemberIdentity.FileName);
        File.WriteAllText(path, content);

        using var fresh = MemberIdentity.LoadOrCreate(_dir);
        Assert.NotNull(fresh.Warning);
        Assert.NotEqual(old, fresh.PublicKey);
        Assert.True(File.Exists(path + ".broken"));
        // 新的已经保存好：再读一次是它，没有提示。
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Null(again.Warning);
        Assert.Equal(fresh.PublicKey, again.PublicKey);
    }

    /// <summary>改了一个字节（加密后的内容被动过）：解不开，同样换新的。</summary>
    [Fact]
    public void TamperedFileGivesNewIdentity()
    {
        using (MemberIdentity.LoadOrCreate(_dir)) { }
        var path = Path.Combine(_dir, MemberIdentity.FileName);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        using var fresh = MemberIdentity.LoadOrCreate(_dir);
        Assert.NotNull(fresh.Warning);
    }

    [Fact]
    public void FingerprintFormat()
    {
        using var identity = MemberIdentity.LoadOrCreate(_dir);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", identity.Fingerprint);
        Assert.Equal(identity.Fingerprint, identity.ToString());
        // 固定输入的指纹固定：SHA-256(32 个零) 的前 10 字节是 66687aadf862bd776c8f，前 10 字节按 Crockford Base32。
        Assert.Equal("CSM7-NBFR-CAYQ-EV4F", MemberIdentity.FingerprintOf(new byte[32]));
    }

    [Fact]
    public void ResetChangesAndPersists()
    {
        using var identity = MemberIdentity.LoadOrCreate(_dir);
        var before = identity.PublicKey;
        identity.Reset();
        Assert.NotEqual(before, identity.PublicKey);
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Equal(identity.PublicKey, again.PublicKey);
    }

    /// <summary>重置时正在用的旧密钥不会被清零：拿着它的握手照样能算完（之后由 Dispose 统一清零）。</summary>
    [Fact]
    public void ResetKeepsOldKeyUsable()
    {
        using var identity = MemberIdentity.LoadOrCreate(_dir);
        var held = identity.KeyPair;
        var before = held.PublicKey.ToArray();
        identity.Reset();
        using var other = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        Assert.Equal(other.Dh(before), held.Dh(other.PublicKey));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
