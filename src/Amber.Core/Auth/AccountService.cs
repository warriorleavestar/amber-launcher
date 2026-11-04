using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Auth;

/// <summary>启动时要用的身份：会话信息 + 外置登录需要的额外 JVM 参数。</summary>
public sealed record ResolvedSession(GameSession Session, IReadOnlyList<string> ExtraJvmArgs);

/// <summary>把账号变成可启动的会话，必要时刷新令牌并保存。</summary>
public sealed class AccountService(HttpClient http, AccountStore store, string dataDir, Func<string?> clientIdProvider)
{
    public AccountStore Store { get; } = store;

    /// <summary>用户配置里的 Client ID 优先，其次环境变量，都没有时用内置的默认值。</summary>
    public string? MicrosoftClientId
    {
        get
        {
            var configured = clientIdProvider();
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var env = Environment.GetEnvironmentVariable(MicrosoftAuth.ClientIdEnvironmentVariable);
            return string.IsNullOrWhiteSpace(env) ? MicrosoftAuth.DefaultClientId : env.Trim();
        }
    }

    public MicrosoftAuth CreateMicrosoftAuth() =>
        new(http, MicrosoftClientId ?? throw new InvalidOperationException(L.T("core.auth.microsoft.missingclientid")));

    public ExternalAuth External { get; } = new(http);

    public Account AddOffline(string name)
    {
        var account = new Account { Kind = AccountKind.Offline, Name = name.Trim(), Uuid = OfflineAccount.OfflineUuid(name.Trim()) };
        Store.AddOrUpdate(account);
        return account;
    }

    public async Task<ResolvedSession> ResolveAsync(Account account, CancellationToken ct = default)
    {
        switch (account.Kind)
        {
            case AccountKind.Offline:
                return new ResolvedSession(OfflineAccount.Create(account.Name), []);

            case AccountKind.Microsoft:
                await CreateMicrosoftAuth().RefreshAsync(account, ct);
                Store.Save();
                return new ResolvedSession(
                    new GameSession(account.Name, account.Uuid, account.AccessToken!, "msa", account.Xuid, MicrosoftClientId ?? ""), []);

            case AccountKind.External:
                await External.EnsureValidAsync(account, ct);
                Store.Save();
                var metadata = await External.GetMetadataAsync(account.ServerUrl!, ct);
                var jar = await new AuthlibInjector(http, Path.Combine(dataDir, "authlib-injector")).EnsureJarAsync(ct);
                return new ResolvedSession(
                    new GameSession(account.Name, account.Uuid, account.AccessToken!, "mojang"),
                    AuthlibInjector.JvmArgs(jar, account.ServerUrl!, metadata));

            default:
                throw new ArgumentOutOfRangeException(nameof(account));
        }
    }

    /// <summary>下载并缓存皮肤，返回 PNG 路径。没有皮肤或下载失败时返回 null（预览退回默认头像）。</summary>
    public async Task<string?> GetSkinFileAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            if (account is { Kind: AccountKind.External, SkinUrl: null }) await External.LoadSkinAsync(account, ct);
            if (account.SkinUrl is not { } url) return null;
            var folder = Path.Combine(dataDir, "skins");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".png");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url, ct), ct);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
