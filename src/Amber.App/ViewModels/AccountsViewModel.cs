using Amber.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Localization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class AccountItem(Account account) : LocalizedObservable
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

    /// <summary>旧版本存下的 http 外置账号：现在不能再用，提示用 https 重新添加。</summary>
    public bool InsecureServer { get; } = ExternalAuth.HasInsecureServer(account);
}

public sealed partial class AccountsViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private CancellationTokenSource? _loginCts;

    public override string Title => L.T("accounts.title");
    public override string Icon => "M12 12a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9zm0 2c-4.4 0-8 2.2-8 5v2h16v-2c0-2.8-3.6-5-8-5z";

    public ObservableCollection<AccountItem> Accounts { get; } = [];
    /// <summary>列表本身不变，换语言时只换文字（不然下拉框的选中序号会被清成 -1）。</summary>
    public OptionItem[] LoginKinds { get; } = OptionItem.Keys("accounts.kind.offline", "accounts.kind.microsoft", "accounts.kind.external");

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
        SetStatus(StatusMessage.Success("accounts.status.removed", item.Name));
        Changed();
    }

    [RelayCommand]
    private void AddOffline()
    {
        var name = OfflineName.Trim();
        if (!IsValidOfflineName(name))
        {
            SetStatus(StatusMessage.Error("accounts.status.invalid_offline_name"));
            return;
        }
        _service.Accounts.AddOffline(name);
        OfflineName = "";
        // 中文之类的名字能用，只提醒一句（规则和新手卡一样）。
        SetStatus(OfflineNameRules.Check(name).NotAscii
            ? StatusMessage.Warning("accounts.status.offline_added_not_ascii", name)
            : StatusMessage.Success("accounts.status.offline_added", name));
        Changed();
    }

    /// <summary>硬性规则见 OfflineNameRules：3–16 个字符、没有空格；非英文字母数字只提醒。</summary>
    public static bool IsValidOfflineName(string name) => OfflineNameRules.Check(name).Ok;

    [RelayCommand]
    private void SaveClientId()
    {
        _service.Settings.MicrosoftClientId = string.IsNullOrWhiteSpace(ClientIdInput) ? null : ClientIdInput.Trim();
        _service.Settings.Save();
        OnPropertyChanged(nameof(MicrosoftMissingClientId));
        SetStatus(StatusMessage.Success("accounts.status.client_id_saved"));
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        SaveClientId();
        if (_service.Accounts.MicrosoftClientId is null)
        {
            SetStatus(StatusMessage.Warning("accounts.status.client_id_missing"));
            return;
        }

        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var auth = _service.Accounts.CreateMicrosoftAuth();
            SetStatus(StatusMessage.Info("accounts.status.requesting_device_code"));
            var code = await auth.BeginDeviceCodeAsync(_loginCts.Token);
            DeviceCode = code.UserCode;
            DeviceUri = code.VerificationUri;
            SetStatus(StatusMessage.Info("accounts.status.awaiting_device_login"));
            var account = await auth.CompleteDeviceCodeAsync(code, new Progress<string>(s => Status = s), _loginCts.Token);
            _service.Accounts.Store.AddOrUpdate(account);
            SetStatus(StatusMessage.Success("accounts.status.logged_in", account.Name));
            Changed();
        }
        catch (OperationCanceledException) when (_loginCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("accounts.status.login_cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("accounts.status.login_failed", e, ErrorText.Of(e)));
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
            SetStatus(StatusMessage.Warning("accounts.status.external_credentials_required"));
            return;
        }
        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        var password = ExternalPassword;
        ExternalPassword = "";
        try
        {
            SetStatus(StatusMessage.Info("accounts.status.connecting_external"));
            var auth = _service.Accounts.External;
            var api = await auth.ResolveApiRootAsync(ServerUrl, _loginCts.Token);
            var result = await auth.LoginAsync(api, ExternalUser.Trim(), password, _loginCts.Token);
            if (result.Account.Uuid == Guid.Empty)
            {
                ExternalProfiles.Clear();
                foreach (var p in result.Profiles) ExternalProfiles.Add(p);
                SelectedProfile = ExternalProfiles[0];
                PendingExternal = result.Account;
                SetStatus(StatusMessage.Info("accounts.status.choose_profile"));
                return;
            }
            FinishExternal(result.Account);
        }
        catch (OperationCanceledException) when (_loginCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("accounts.status.login_cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("accounts.status.login_failed", e, ErrorText.Of(e)));
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
            SetStatus(StatusMessage.Failure("accounts.status.profile_selection_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FinishExternal(Account account)
    {
        _service.Accounts.Store.AddOrUpdate(account);
        SetStatus(StatusMessage.Success("accounts.status.external_logged_in", account.Name, account.ServerName));
        Changed();
    }

    public Task RunSnapshotDemoAsync()
    {
        LoginTab = 1;
        return Task.CompletedTask;
    }
}
