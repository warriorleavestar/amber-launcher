using Amber.Core.Localization;
using Amber.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Auth;

public enum AccountKind { Offline, Microsoft, External }

/// <summary>保存在本地的一个账号。令牌在写盘时加密（见 <see cref="AccountStore"/>），密码永不保存。</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Guid Uuid { get; set; }

    [JsonIgnore] public string? AccessToken { get; set; }
    [JsonIgnore] public string? RefreshToken { get; set; }
    public string? ProtectedAccessToken { get; set; }
    public string? ProtectedRefreshToken { get; set; }

    public DateTimeOffset? AccessTokenExpires { get; set; }
    public string? Xuid { get; set; }
    public string? SkinUrl { get; set; }
    public bool SlimSkin { get; set; }

    // 外置登录
    public string? ServerUrl { get; set; }
    public string? ServerName { get; set; }
    public string? ClientToken { get; set; }

    [JsonIgnore]
    public string KindText => Kind switch
    {
        AccountKind.Offline => L.T("core.auth.account.offline"),
        AccountKind.Microsoft => L.T("core.auth.account.microsoft"),
        AccountKind.External => ServerName is { Length: > 0 } s ? L.F("core.auth.account.namedexternal", s) : L.T("core.auth.account.external"),
        _ => "",
    };

    public override string ToString() => $"{Name}（{KindText}）";
}

/// <summary>账号列表，保存在 accounts.json。Windows 上令牌用 DPAPI 绑定当前用户加密。</summary>
public sealed class AccountStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AccountKind>() },
        TypeInfoResolver = AmberJsonContext.Resolver,
    };

    private static readonly byte[] Entropy = "Amber.Launcher.Accounts"u8.ToArray();

    public List<Account> Accounts { get; private set; } = [];
    public string? SelectedId { get; set; }

    public Account? Selected => Accounts.FirstOrDefault(a => a.Id == SelectedId) ?? Accounts.FirstOrDefault();

    internal sealed class FileModel
    {
        public string? SelectedId { get; set; }
        public List<Account> Accounts { get; set; } = [];
    }

    public void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var model = JsonSerializer.Deserialize(File.ReadAllText(path), Options.Info<FileModel>()) ?? new FileModel();
            foreach (var account in model.Accounts)
            {
                account.AccessToken = Unprotect(account.ProtectedAccessToken);
                account.RefreshToken = Unprotect(account.ProtectedRefreshToken);
            }
            Accounts = model.Accounts;
            SelectedId = model.SelectedId;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", true);
        }
    }

    public void Save()
    {
        foreach (var account in Accounts)
        {
            account.ProtectedAccessToken = Protect(account.AccessToken);
            account.ProtectedRefreshToken = Protect(account.RefreshToken);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel { SelectedId = SelectedId, Accounts = Accounts }, Options.Info<FileModel>()));
        File.Move(temp, path, true);
    }

    /// <summary>同类型同 UUID（外置还要同服务器）的账号视为同一个，更新而不是新增。</summary>
    public void AddOrUpdate(Account account)
    {
        var existing = Accounts.FindIndex(a => a.Kind == account.Kind && a.Uuid == account.Uuid && a.ServerUrl == account.ServerUrl);
        if (existing >= 0)
        {
            account.Id = Accounts[existing].Id;
            Accounts[existing] = account;
        }
        else
        {
            Accounts.Add(account);
        }
        SelectedId = account.Id;
        Save();
    }

    public void Remove(Account account)
    {
        Accounts.RemoveAll(a => a.Id == account.Id);
        if (SelectedId == account.Id) SelectedId = Accounts.FirstOrDefault()?.Id;
        Save();
    }

    private static string? Protect(string? secret)
    {
        if (secret is null) return null;
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? stored)
    {
        if (stored is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // 换了 Windows 用户或文件被改动：当作令牌失效，要求重新登录。
            return null;
        }
    }
}

public sealed class LoginRequiredException(string message) : Exception(message);
