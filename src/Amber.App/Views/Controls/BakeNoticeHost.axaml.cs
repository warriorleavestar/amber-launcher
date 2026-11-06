using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.Views.Controls;

/// <summary>烘焙失败的紧凑提示，放在启动状态框内。</summary>
public partial class BakeNoticeHost : UserControl
{
    public BakeNoticeHost()
    {
        InitializeComponent();
        DataContext = Services.Toasts.BakeNotices ?? new BakeNoticeState();
    }
}

/// <summary>通知属于窗口，启动页重新创建时仍延续原计时和原实例回调。</summary>
public sealed class BakeNoticeState : ObservableObject
{
    public ObservableCollection<BakeNotice> Notices { get; } = [];
    public BakeNotice? Current => Notices.FirstOrDefault();
    public bool HasNotice => Current is not null;

    public BakeNoticeState() => Notices.CollectionChanged += (_, _) =>
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasNotice));
    };

    internal BakeNotice Show(string text, Action retry)
    {
        if (Notices.Count == 3) Notices[^1].DismissCommand.Execute(null);
        var notice = new BakeNotice(text, retry, item => Notices.Remove(item));
        Notices.Insert(0, notice);
        return notice;
    }

    internal void Clear()
    {
        foreach (var notice in Notices.ToArray()) notice.DismissCommand.Execute(null);
    }
}

/// <summary>每条通知独立结束；按钮重复触发、过期或被挤出后都不会再次启动。</summary>
public sealed class BakeNotice
{
    private readonly Action _retry;
    private readonly Action<BakeNotice> _remove;
    private readonly IDisposable _expiry;
    private bool _active = true;
    public string Text { get; }
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand DismissCommand { get; }

    internal BakeNotice(string text, Action retry, Action<BakeNotice> remove)
    {
        Text = text;
        _retry = retry;
        _remove = remove;
        RetryCommand = new RelayCommand(() => Finish(true));
        DismissCommand = new RelayCommand(() => Finish(false));
        _expiry = DispatcherTimer.RunOnce(() => Finish(false), Services.Toasts.UndoDuration);
    }

    private void Finish(bool retry)
    {
        if (!_active) return;
        _active = false;
        _expiry.Dispose();
        _remove(this);
        if (retry) _retry();
    }
}
