using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class AccountItem(Account account) : ObservableObject
{
    public Account Account { get; } = account;
    public string Name => Account.Name;
    public string KindText => Account.KindText;
    public string Initial => Account.Name.Length > 0 ? Account.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAvatar))]
    public partial Bitmap? Head { get; set; }

    [ObservableProperty]
    public partial Bitmap? Body { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public bool HasNoAvatar => Head is null;
}

public sealed partial class AccountsViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private CancellationTokenSource? _loginCts;

    public override string Title => "账号";
    public override string Icon => "☺";

    public ObservableCollection<AccountItem> Accounts { get; } = [];
    public string[] LoginKinds { get; } = ["离线", "微软", "外置"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOfflineTab), nameof(IsMicrosoftTab), nameof(IsExternalTab))]
    public partial int LoginTab { get; set; }

    public bool IsOfflineTab => LoginTab == 0;
    public bool IsMicrosoftTab => LoginTab == 1;
    public bool IsExternalTab => LoginTab == 2;

    [ObservableProperty]
    public partial AccountItem? Current { get; set; }

    [ObservableProperty]
    public partial string OfflineName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    public partial string? DeviceCode { get; set; }

    [ObservableProperty]
    public partial string? DeviceUri { get; set; }

    public bool HasDeviceCode => DeviceCode is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrosoftMissingClientId))]
    public partial string ClientIdInput { get; set; }

    public bool MicrosoftMissingClientId => string.IsNullOrWhiteSpace(ClientIdInput) && _service.Accounts.MicrosoftClientId is null;

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string ExternalUser { get; set; } = "";

    [ObservableProperty]
    public partial string ExternalPassword { get; set; } = "";

    public ObservableCollection<ExternalProfile> ExternalProfiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsProfileChoice))]
    public partial Account? PendingExternal { get; set; }

    public bool NeedsProfileChoice => PendingExternal is not null;

    [ObservableProperty]
    public partial ExternalProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public AccountsViewModel(LauncherService service)
    {
        _service = service;
        ClientIdInput = service.Settings.MicrosoftClientId ?? "";
    }

    protected override void OnFirstActivated()
    {
        _active = true;
        Rebuild();
    }

    private bool _active;

    private void Rebuild()
    {
        if (!_active) return;
        var selected = _service.Accounts.Store.Selected;
        Accounts.Clear();
        foreach (var account in _service.Accounts.Store.Accounts)
        {
            var item = new AccountItem(account) { IsCurrent = account.Id == selected?.Id };
            Accounts.Add(item);
            _ = LoadSkinAsync(item);
        }
        Current = Accounts.FirstOrDefault(a => a.IsCurrent);
    }

    private async Task LoadSkinAsync(AccountItem item)
    {
        var path = await _service.Accounts.GetSkinFileAsync(item.Account);
        if (path is null) return;
        if (await Task.Run(() => SkinRenderer.Render(path, item.Account.SlimSkin)) is { } rendered)
        {
            item.Head = rendered.Head;
            item.Body = rendered.Body;
        }
    }

    private void Changed()
    {
        Rebuild();
        _service.NotifyAccountsChanged();
    }

    [RelayCommand]
    private void Use(AccountItem? item)
    {
        if (item is null) return;
        _service.Accounts.Store.SelectedId = item.Account.Id;
        _service.Accounts.Store.Save();
        Changed();
    }

    [RelayCommand]
    private void Remove(AccountItem? item)
    {
        if (item is null) return;
        _service.Accounts.Store.Remove(item.Account);
        Status = $"已移除 {item.Name}（只删本地记录，账号本身不受影响）";
        Changed();
    }

    [RelayCommand]
    private void AddOffline()
    {
        var name = OfflineName.Trim();
        if (!IsValidOfflineName(name))
        {
            Status = "离线玩家名只能用 3 到 16 位字母、数字和下划线";
            return;
        }
        _service.Accounts.AddOffline(name);
        OfflineName = "";
        Status = $"已添加离线账号 {name}";
        Changed();
    }

    public static bool IsValidOfflineName(string name) =>
        name.Length is >= 3 and <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    [RelayCommand]
    private void SaveClientId()
    {
        _service.Settings.MicrosoftClientId = string.IsNullOrWhiteSpace(ClientIdInput) ? null : ClientIdInput.Trim();
        _service.Settings.Save();
        OnPropertyChanged(nameof(MicrosoftMissingClientId));
        Status = "已保存 Client ID";
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        SaveClientId();
        if (_service.Accounts.MicrosoftClientId is null)
        {
            Status = "请先填写你自己的 Azure 应用 Client ID";
            return;
        }

        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var auth = _service.Accounts.CreateMicrosoftAuth();
            Status = "正在向微软申请设备码……";
            var code = await auth.BeginDeviceCodeAsync(_loginCts.Token);
            DeviceCode = code.UserCode;
            DeviceUri = code.VerificationUri;
            Status = "请在浏览器里输入上面的代码并登录，这里会自动继续";
            var account = await auth.CompleteDeviceCodeAsync(code, new Progress<string>(s => Status = s), _loginCts.Token);
            _service.Accounts.Store.AddOrUpdate(account);
            Status = $"已登录 {account.Name}";
            Changed();
        }
        catch (OperationCanceledException)
        {
            Status = "已取消登录";
        }
        catch (Exception e)
        {
            Status = "登录失败：" + e.Message;
        }
        finally
        {
            DeviceCode = null;
            DeviceUri = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDeviceUri()
    {
        if (DeviceUri is { } uri) Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CancelLogin() => _loginCts?.Cancel();

    [RelayCommand]
    private async Task LoginExternalAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(ExternalUser) || string.IsNullOrEmpty(ExternalPassword))
        {
            Status = "请填写服务器地址、账号和密码";
            return;
        }
        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        var password = ExternalPassword;
        ExternalPassword = "";
        try
        {
            Status = "正在连接皮肤站……";
            var auth = _service.Accounts.External;
            var api = await auth.ResolveApiRootAsync(ServerUrl, _loginCts.Token);
            var result = await auth.LoginAsync(api, ExternalUser.Trim(), password, _loginCts.Token);
            if (result.Account.Uuid == Guid.Empty)
            {
                ExternalProfiles.Clear();
                foreach (var p in result.Profiles) ExternalProfiles.Add(p);
                SelectedProfile = ExternalProfiles[0];
                PendingExternal = result.Account;
                Status = "这个账号有多个角色，请选择一个";
                return;
            }
            FinishExternal(result.Account);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消登录";
        }
        catch (Exception e)
        {
            Status = "登录失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmProfileAsync()
    {
        if (PendingExternal is not { } account || SelectedProfile is not { } profile) return;
        IsBusy = true;
        try
        {
            await _service.Accounts.External.SelectProfileAsync(account, profile);
            PendingExternal = null;
            FinishExternal(account);
        }
        catch (Exception e)
        {
            Status = "选择角色失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FinishExternal(Account account)
    {
        _service.Accounts.Store.AddOrUpdate(account);
        Status = $"已登录 {account.Name}（{account.ServerName}）";
        Changed();
    }

    public Task RunSnapshotDemoAsync()
    {
        LoginTab = 1;
        return Task.CompletedTask;
    }
}
