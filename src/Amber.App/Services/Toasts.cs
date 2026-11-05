using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>
/// 右下角的 Toast（改版规格 5.4）：最多 3 条。删除后显示“已删除 X【撤销】”8 秒；
/// Toast 消失后，本次运行里删过的东西还能用 Ctrl+Z（最近一次）撤销。
/// 文件进回收目录的用 Deleted；列表里的一项（服务器、节点）用 Removed，自己给出怎么放回去。
/// </summary>
public static class Toasts
{
    public static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(8);

    private static WindowNotificationManager? _manager;

    /// <summary>一条撤销记录：Toast 上显示的名字，和撤销时做的事（返回恢复后的名字，失败抛异常）。</summary>
    private sealed record UndoItem(string Name, Func<string> Undo);

    /// <summary>撤销记录：最近的在最后。</summary>
    private static readonly List<UndoItem> History = [];

    public static void Attach(Window window) =>
        _manager = new WindowNotificationManager(window) { Position = NotificationPosition.BottomRight, MaxItems = 3 };

    public static void Show(string text, NotificationType type = NotificationType.Information) =>
        Dispatcher.UIThread.Post(() => _manager?.Show(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 }, type, TimeSpan.FromSeconds(4)));

    /// <summary>文件进回收目录后调用：记下撤销信息并显示 Toast。restored 收到实际恢复到的路径。</summary>
    public static void Deleted(TrashEntry entry, Action<string> restored) =>
        Add(new UndoItem(entry.Name, () =>
        {
            var path = TrashService.Restore(entry);
            restored(path);
            return Path.GetFileName(path);
        }));

    /// <summary>列表里的一项删掉后调用（不是文件）：undo 把它放回去。</summary>
    public static void Removed(string name, Action undo) =>
        Add(new UndoItem(name, () =>
        {
            undo();
            return name;
        }));

    private static void Add(UndoItem item)
    {
        lock (History) History.Add(item);
        Dispatcher.UIThread.Post(() =>
        {
            if (_manager is null) return;
            var undo = new Button { Content = L.T("common.undo"), Classes = { "secondary" }, Margin = new Avalonia.Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var content = new DockPanel
            {
                Children =
                {
                    undo,
                    new TextBlock { Text = L.F("trash.deleted", item.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            DockPanel.SetDock(undo, Dock.Right);
            undo.Click += (_, _) =>
            {
                undo.IsEnabled = false;
                Undo(item);
            };
            _manager.Show(content, NotificationType.Information, UndoDuration);
        });
    }

    /// <summary>撤销最近一次删除（Ctrl+Z）。没有可撤销的返回 false。</summary>
    public static bool UndoLatest()
    {
        UndoItem? item;
        lock (History) item = History.Count > 0 ? History[^1] : null;
        if (item is null) return false;
        Undo(item);
        return true;
    }

    private static void Undo(UndoItem item)
    {
        lock (History)
        {
            if (!History.Remove(item)) return;
        }
        try
        {
            Show(L.F("trash.restored", item.Undo()), NotificationType.Success);
        }
        catch (TrashException e)
        {
            Show(L.F("trash.restore_failed", Describe(e)), NotificationType.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show(L.F("trash.restore_failed", ErrorText.Describe(e)), NotificationType.Error);
        }
    }

    /// <summary>回收目录出错的原因写成当前语言的话。</summary>
    public static string Describe(TrashException e) => e.Reason switch
    {
        TrashFailure.SourceMissing => L.F("trash.failure.source_missing", Path.GetFileName(e.PathInvolved)),
        TrashFailure.NoTrashLocation => L.T("trash.failure.no_location"),
        TrashFailure.IsTrashItself => L.T("trash.failure.is_trash"),
        TrashFailure.TrashInScannedFolder => L.T("trash.failure.in_scanned"),
        TrashFailure.OriginalOutsideOwner => L.T("trash.failure.outside_owner"),
        TrashFailure.EntryMissing => L.T("trash.failure.entry_missing"),
        _ => e.Message,
    };
}
