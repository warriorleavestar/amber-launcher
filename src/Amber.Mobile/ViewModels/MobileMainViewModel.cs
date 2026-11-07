using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.Mobile.ViewModels;

public sealed partial class MobileServerItem : ObservableObject
{
    private readonly MobileMainViewModel _owner;

    public MobileServerItem(JayServerList.Entry entry, MobileMainViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        ConnectCommand = new AsyncRelayCommand(() => _owner.ConnectAsync(this));
        RemoveCommand = new AsyncRelayCommand(() => _owner.RemoveAsync(this));
    }

    public JayServerList.Entry Entry { get; }
    public string Title => Entry.DisplayName;
    public string Detail => L.F("multiplayer.jayserver.item_detail", Entry.Username, Entry.Host, Entry.Port);
    public string ConnectText => IsConnected ? L.T("mobile.server.connected") : L.T("mobile.server.connect");
    public string RemoveText => L.T("common.remove");
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectText))]
    public partial bool IsConnected { get; set; }
}

public sealed partial class MobileMainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly JayServerList _serverList;
    private readonly MemberIdentity _identity;
    private TunnelClient? _tunnel;
    private MobileServerItem? _connected;

    public MobileMainViewModel(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jay");
        Directory.CreateDirectory(_dataDirectory);
        _serverList = new JayServerList(_dataDirectory);
        _identity = MemberIdentity.LoadOrCreate(_dataDirectory);
        DeviceFingerprint = _identity.Fingerprint;
        if (_identity.Warning is { Length: > 0 } warning) Status = warning;
        ReloadServers();
    }

    public ObservableCollection<MobileServerItem> Servers { get; } = [];

    public string Title => "Jay";
    private static string PlatformName => OperatingSystem.IsIOS()
        ? L.T("mobile.platform.ios")
        : OperatingSystem.IsAndroid()
            ? L.T("mobile.platform.android")
            : L.T("mobile.platform.mobile");

    private static string PlatformString(string key) =>
        L.T(OperatingSystem.IsIOS() ? key + ".ios" : OperatingSystem.IsAndroid() ? key + ".android" : key + ".mobile");

    public string VersionText => L.F("mobile.version", PlatformName, "0.7.0");
    public string ExperimentalText => L.T("mobile.experimental");
    public string ScopeTitle => L.T("mobile.scope.title");
    public string ScopeDescription => PlatformString("mobile.scope.description");
    public string DeviceTitle => L.T("mobile.device.title");
    public string DeviceHint => L.T("mobile.device.hint");
    public string EnrollTitle => L.T("mobile.enroll.title");
    public string EnrollHint => L.T("mobile.enroll.hint");
    public string EnrollButtonText => L.T("mobile.enroll.button");
    public string TunnelTitle => L.T("mobile.tunnel.title");
    public string TunnelHint => PlatformString("mobile.tunnel.hint");
    public string DisconnectButtonText => L.T("mobile.tunnel.disconnect");
    public string SavedServersTitle => L.T("mobile.servers.title");
    public string NoServersText => L.T("mobile.servers.empty");
    public bool HasNoServers => Servers.Count == 0;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    [ObservableProperty] public partial string DeviceFingerprint { get; set; } = "";
    [ObservableProperty] public partial string InviteText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string LocalAddress { get; set; } = "";

    [RelayCommand]
    private async Task EnrollAsync()
    {
        var invite = GateInvite.TryParse(InviteText, out var error);
        if (invite is null)
        {
            Status = error ?? L.T("mobile.enroll.invalid");
            return;
        }

        IsBusy = true;
        Status = L.F("multiplayer.jayserver.enrolling", invite.Host);
        try
        {
            var membership = await GateClient.EnrollAsync(invite, _identity);
            var entry = _serverList.Add(membership);
            InviteText = "";
            ReloadServers();
            Status = L.F("multiplayer.jayserver.enrolled", entry.DisplayName, membership.Username);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or CryptographicException or GateRejectedException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ConnectAsync(MobileServerItem item)
    {
        if (item.IsConnected) return;
        await DisconnectCoreAsync();
        try
        {
            var membership = _serverList.ToMembership(item.Entry);
            var entryId = item.Entry.Id;
            _tunnel = new TunnelClient(0, GateClient.Opener(membership, _identity,
                network => _serverList.UpdateNetwork(entryId, network)),
                line => Dispatcher.UIThread.Post(() => Status = line));
            _connected = item;
            item.IsConnected = true;
            IsConnected = true;
            LocalAddress = $"127.0.0.1:{_tunnel.Port}";
            Status = L.F("mobile.tunnel.ready", item.Title, LocalAddress);
        }
        catch (Exception e) when (e is IOException or FormatException or CryptographicException or System.Net.Sockets.SocketException)
        {
            Status = L.F("multiplayer.jayserver.enroll_failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await DisconnectCoreAsync();
        Status = L.T("multiplayer.jayserver.disconnected");
    }

    internal async Task RemoveAsync(MobileServerItem item)
    {
        if (ReferenceEquals(item, _connected)) await DisconnectCoreAsync();
        _serverList.Remove(item.Entry.Id);
        Servers.Remove(item);
        OnPropertyChanged(nameof(HasNoServers));
    }

    private async Task DisconnectCoreAsync()
    {
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
            _tunnel = null;
        }
        if (_connected is not null) _connected.IsConnected = false;
        _connected = null;
        IsConnected = false;
        LocalAddress = "";
    }

    private void ReloadServers()
    {
        Servers.Clear();
        foreach (var entry in _serverList.Load()) Servers.Add(new MobileServerItem(entry, this));
        OnPropertyChanged(nameof(HasNoServers));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectCoreAsync();
        _identity.Dispose();
    }
}
