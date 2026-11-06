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

    /// <summary>重置后旧密钥还没被清零：正在进行的握手可以用完（到 Dispose 才清）。</summary>
    [Fact]
    public void ResetKeepsOldKeyUsableUntilDispose()
    {
        using var identity = MemberIdentity.LoadOrCreate(_dir);
        var old = identity.KeyPair;
        identity.Reset();
        using var other = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        Assert.Equal(32, old.Dh(other.PublicKey).Length);
        Assert.NotEqual(old.PublicKey, identity.PublicKey);
    }

    /// <summary>非 Windows：已存在的包裹密钥文件权限过宽时被收紧到 0600；已存在的那把会被沿用（不被覆盖）。</summary>
    [Fact]
    public void ExistingWrapKeyIsReusedAndTightened()
    {
        if (OperatingSystem.IsWindows()) return;
        byte[] first;
        using (var identity = MemberIdentity.LoadOrCreate(_dir)) first = identity.PublicKey;
        var wrap = Path.Combine(_dir, "identity.wrap");
        File.SetUnixFileMode(wrap, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Equal(first, again.PublicKey);
        Assert.Null(again.Warning);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(wrap));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, MemberIdentity.FileName)));
    }

    /// <summary>读写出错（这里用“身份文件是个目录”模拟）：不覆盖、不生成 .broken，这次用临时身份并提示。</summary>
    [Fact]
    public void IoErrorUsesTemporaryIdentityWithoutOverwriting()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, MemberIdentity.FileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) return; // 只有 Windows 的共享锁会让别的读取失败
            using var identity = MemberIdentity.LoadOrCreate(_dir);
            Assert.False(identity.IsPersisted);
            Assert.NotNull(identity.Warning);
            Assert.False(File.Exists(path + ".broken"));
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    /// <summary>非 Windows：包裹密钥文件长度不对（比如断电留下的空文件）时，换新身份并重建包裹密钥，不会每次启动都失败。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrokenWrapKeyRecovers(bool identityExists)
    {
        if (OperatingSystem.IsWindows()) return;
        if (identityExists) using (MemberIdentity.LoadOrCreate(_dir)) { }
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "identity.wrap"), []);
        byte[] recovered;
        using (var identity = MemberIdentity.LoadOrCreate(_dir))
        {
            Assert.Equal(identityExists, identity.Warning is not null);
            Assert.True(identity.IsPersisted);
            recovered = identity.PublicKey;
        }
        Assert.Equal(32, new FileInfo(Path.Combine(_dir, "identity.wrap")).Length);
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Null(again.Warning);
        Assert.Equal(recovered, again.PublicKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
