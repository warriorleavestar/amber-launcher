using System.Collections.Specialized;
using System.ComponentModel;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Amber.App.Views;

public partial class LogPage : UserControl
{
    private LogViewModel? _vm;
    private bool _scrollQueued;

    public LogPage() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Bind(DataContext as LogViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as LogViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Bind(null);
    }

    private void Bind(LogViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged -= OnLines;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.JumpRequested -= OnJump;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged += OnLines;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.JumpRequested += OnJump;
            QueueScroll();
        }
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e) => QueueScroll();

    // 筛选切换后列表要先重建，所以放到下一轮再滚。
    private void OnJump(LogEntry entry) => Dispatcher.UIThread.Post(() => List.ScrollIntoView(entry), DispatcherPriority.Background);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogViewModel.FollowLatest) or null) QueueScroll();
    }

    private void QueueScroll()
    {
        if (_vm is not { FollowLatest: true } || _scrollQueued) return;
        _scrollQueued = true;
        // 一批日志只滚一次，放到布局之后。
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm is { FollowLatest: true, Lines.Count: > 0 } vm) List.ScrollIntoView(vm.Lines.Count - 1);
        }, DispatcherPriority.Background);
    }
}
