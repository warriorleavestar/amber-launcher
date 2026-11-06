using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.Views.Controls;

/// <summary>仅用于烘焙失败的短提示；在主内容区排版，不遮挡启动操作。</summary>
public partial class BakeNoticeHost : UserControl
{
    public ObservableCollection<BakeNotice> Notices { get; } = [];

    public BakeNoticeHost() => InitializeComponent();

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
