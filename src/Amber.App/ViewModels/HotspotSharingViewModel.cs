using System.Diagnostics;
using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>手机热点共享的界面状态；网络配置和恢复由联机会话的提权守护负责。</summary>
public sealed partial class HotspotSharingViewModel : LocalizedObservable
{
    private readonly SemaphoreSlim _switchLock;
    private readonly Action _openSettings;
    private IHotspotSharingSession? _session;
    private bool _fullLan;
    private string? _exitIp;
    private CancellationToken _sessionToken;
    private CancellationTokenSource? _operation;
    private int _revision;
    private StatusMessage? _feedback;

    public HotspotSharingViewModel(SemaphoreSlim switchLock)
        : this(switchLock, OperatingSystem.IsWindows(), () =>
            Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true })) { }

    internal HotspotSharingViewModel(SemaphoreSlim switchLock, bool supported, Action openSettings)
    {
        _switchLock = switchLock;
        IsSupported = supported;
        _openSettings = openSettings;
    }

    public bool IsSupported { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsSharing { get; private set; }

    public bool CanStart => IsSupported && !IsBusy && !IsSharing && Ready;
    public bool CanStop => IsSupported && !IsBusy && IsSharing && _session is not null;
    private bool Ready => _session is not null && _fullLan && !string.IsNullOrEmpty(_exitIp) && !_sessionToken.IsCancellationRequested;
    public string PrerequisiteText => L.T(!IsSupported ? "multiplayer.hotspot.windows_only"
        : !_fullLan ? "multiplayer.hotspot.requires_lan"
        : string.IsNullOrEmpty(_exitIp) || _session is null ? "multiplayer.hotspot.requires_exit"
        : "multiplayer.hotspot.ready");
    public string StatusText => _feedback?.Text ?? "";
    public bool HasStatus => _feedback is not null;
    public bool HasError => _feedback?.Kind == StatusKind.Error;

    /// <summary>只展示守护已确认的状态。换会话或换出口会使旧请求失效，迟到的回执不能重新点亮共享。</summary>
    internal void UpdateSession(IHotspotSharingSession? session, bool fullLan, string? exitIp, CancellationToken token = default)
    {
        var changed = !ReferenceEquals(_session, session) || _fullLan != fullLan || _exitIp != exitIp || _sessionToken != token;
        var wasSharing = IsSharing;
        if (changed)
        {
            _revision++;
            _operation?.Cancel();
            _session = session;
            _fullLan = fullLan;
            _exitIp = exitIp;
            _sessionToken = token;
        }
        if (!IsBusy || changed)
        {
            IsSharing = IsSupported && Ready && session?.HotspotSharingActive == true;
            if (wasSharing && !IsSharing) SetFeedback(StatusMessage.Info("multiplayer.hotspot.stopped_automatically"));
            else if (changed && IsBusy) SetFeedback(StatusMessage.Info("multiplayer.hotspot.cancelled"));
        }
        NotifyContextChanged();
    }

    private void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(PrerequisiteText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void SetFeedback(StatusMessage? feedback)
    {
        _feedback = feedback;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasError));
    }

    [RelayCommand(CanExecute = nameof(IsSupported))]
    private void OpenSettings()
    {
        if (!IsSupported) return;
        try { _openSettings(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetFeedback(StatusMessage.Failure("multiplayer.hotspot.settings_failed", ex, ErrorText.Of(ex)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => ChangeAsync(true);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => ChangeAsync(false);

    private async Task ChangeAsync(bool enabled)
    {
        if (enabled ? !CanStart : !CanStop) return;
        var session = _session!;
        var revision = _revision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
        _operation = operation;
        IsBusy = true;
        SetFeedback(StatusMessage.Info(enabled ? "multiplayer.hotspot.starting" : "multiplayer.hotspot.stopping"));
        var locked = false;
        try
        {
            await _switchLock.WaitAsync(operation.Token);
            locked = true;
            if (revision != _revision || !ReferenceEquals(session, _session)) return;
            await session.SetHotspotSharingAsync(enabled, operation.Token);
            if (revision != _revision || !ReferenceEquals(session, _session) || operation.IsCancellationRequested) return;
            IsSharing = session.HotspotSharingActive;
            if (IsSharing != enabled) throw new IOException(L.T("multiplayer.hotspot.not_confirmed"));
            SetFeedback(StatusMessage.Info(enabled ? "multiplayer.hotspot.configured" : "multiplayer.hotspot.stopped"));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (revision == _revision) SetFeedback(StatusMessage.Info("multiplayer.hotspot.cancelled"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or TimeoutException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            if (revision == _revision && ReferenceEquals(session, _session))
            {
                IsSharing = session.HotspotSharingActive;
                SetFeedback(StatusMessage.Failure("multiplayer.hotspot.failed", ex, ErrorText.Of(ex)));
            }
        }
        finally
        {
            if (locked) _switchLock.Release();
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                IsBusy = false;
            }
        }
    }
}
