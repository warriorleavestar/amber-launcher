using System.Collections.Specialized;
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
        if (_vm is not null) _vm.Lines.CollectionChanged -= OnLines;
        _vm = DataContext as LogViewModel;
        if (_vm is not null) _vm.Lines.CollectionChanged += OnLines;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.Lines.CollectionChanged -= OnLines;
        _vm = null;
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not { FollowLatest: true } || _scrollQueued) return;
        _scrollQueued = true;
        // 一批日志只滚一次，放到布局之后。
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm is { Lines.Count: > 0 } vm) List.ScrollIntoView(vm.Lines.Count - 1);
        }, DispatcherPriority.Background);
    }
}
