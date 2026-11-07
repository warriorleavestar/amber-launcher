using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>保存的一台 Jay 服务器。</summary>
public sealed partial class JayServerItem(JayServerList.Entry entry) : ObservableObject
{
    public JayServerList.Entry Entry { get; } = entry;
    public string Title => $"{Entry.DisplayName}";
    public string Detail => L.F("multiplayer.jayserver.item_detail", Entry.Username, Entry.Host, Entry.Port);
    [ObservableProperty] public partial bool IsConnected { get; set; }
}

/// <summary>
/// 联机页里的“Jay 服务器”：显示本机设备码，粘贴邀请码加入，之后一键进服。
/// 进服时在本机开一个临时端口，游戏连 127.0.0.1:端口，每条连接经网关握手（设备密钥 + 访问密钥）后转到服务器。
/// </summary>
public sealed partial class JayServersViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private readonly Func<string?> _instance;
    private readonly Action? _navigateHome;
    private readonly Func<MemberIdentity> _loadIdentity;
    private readonly JayServerList _list = new(AppSettings.DataDir);
    private MemberIdentity? _identity;
    private TunnelClient? _tunnel;
    private JayServerItem? _connected;

    public ObservableCollection<JayServerItem> Servers { get; } = [];
    [ObservableProperty] public partial string InviteText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string DeviceCode { get; set; } = "";

    /// <param name="identity">本机联机身份：和房间邀请共用同一个对象（重置身份时两边一起换）。</param>
    public JayServersViewModel(LauncherService service, Func<string?> instance, Action? navigateHome, Func<MemberIdentity> identity)
    {
        _loadIdentity = identity;
        _service = service;
        _instance = instance;
        _navigateHome = navigateHome;
    }

    public void Load()
    {
        Identity();
        Servers.Clear();
        foreach (var e in _list.Load()) Servers.Add(new JayServerItem(e));
    }

    private MemberIdentity Identity()
    {
        if (_identity is null)
        {
            _identity = _loadIdentity();
            DeviceCode = L.F("multiplayer.jayserver.device_code", _identity.Fingerprint);
            if (_identity.Warning is { } warning) Status = warning;
        }
        return _identity;
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        var invite = GateInvite.TryParse(InviteText, out var error);
        if (invite is null)
        {
            Status = error ?? "";
            return;
        }
        IsBusy = true;
        Status = L.F("multiplayer.jayserver.enrolling", invite.Host);
        try
        {
            var membership = await GateClient.EnrollAsync(invite, Identity());
            var entry = _list.Add(membership);
            InviteText = "";
            Load();
            Status = L.F("multiplayer.jayserver.enrolled", entry.DisplayName, membership.Username);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException or GateRejectedException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync(JayServerItem item)
    {
        if (_instance() is not { } id)
        {
            Status = L.T("multiplayer.jayserver.no_instance");
            return;
        }
        await DisconnectAsync();
        try
        {
            var membership = _list.ToMembership(item.Entry);
            var entryId = item.Entry.Id;
            // 网关每次进服都会回最新的隐私等级和节点列表：存下来，下次直接用。
            _tunnel = new TunnelClient(0, GateClient.Opener(membership, Identity(), network => _list.UpdateNetwork(entryId, network)), line => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = line));
            _connected = item;
            item.IsConnected = true;
            var address = $"127.0.0.1:{_tunnel.Port}";
            Status = L.F("multiplayer.jayserver.connected", item.Title, address, item.Entry.Username);
            _service.RequestLaunch(new LaunchRequest(id, JoinServer: address));
            _navigateHome?.Invoke();
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or System.Net.Sockets.SocketException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
            _tunnel = null;
        }
        if (_connected is not null)
        {
            _connected.IsConnected = false;
            _connected = null;
            Status = L.T("multiplayer.jayserver.disconnected");
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(JayServerItem item)
    {
        if (ReferenceEquals(item, _connected)) await DisconnectAsync();
        _list.Remove(item.Entry.Id);
        Servers.Remove(item);
    }
}
