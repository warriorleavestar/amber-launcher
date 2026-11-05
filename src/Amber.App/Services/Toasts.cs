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
/// </summary>
public static class Toasts
{
    public static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(8);

    private static WindowNotificationManager? _manager;

    /// <summary>撤销记录：最近的在最后。每项是“恢复后要做的事”（比如刷新列表）。</summary>
    private static readonly List<(TrashEntry Entry, Action<string> Restored)> History = [];

    public static void Attach(Window window) =>
        _manager = new WindowNotificationManager(window) { Position = NotificationPosition.BottomRight, MaxItems = 3 };

    public static void Show(string text, NotificationType type = NotificationType.Information) =>
        Dispatcher.UIThread.Post(() => _manager?.Show(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 }, type, TimeSpan.FromSeconds(4)));

    /// <summary>删除后调用：记下撤销信息并显示 Toast。restored 收到实际恢复到的路径。</summary>
    public static void Deleted(TrashEntry entry, Action<string> restored)
    {
        lock (History) History.Add((entry, restored));
        Dispatcher.UIThread.Post(() =>
        {
            if (_manager is null) return;
            var undo = new Button { Content = L.T("common.undo"), Classes = { "secondary" }, Margin = new Avalonia.Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var content = new DockPanel
            {
                Children =
                {
                    undo,
                    new TextBlock { Text = L.F("trash.deleted", entry.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            DockPanel.SetDock(undo, Dock.Right);
            undo.Click += (_, _) =>
            {
                undo.IsEnabled = false;
                Undo(entry);
            };
            _manager.Show(content, NotificationType.Information, UndoDuration);
        });
    }

    /// <summary>撤销最近一次删除（Ctrl+Z）。没有可撤销的返回 false。</summary>
    public static bool UndoLatest()
    {
        TrashEntry? entry;
        lock (History) entry = History.Count > 0 ? History[^1].Entry : null;
        if (entry is null) return false;
        Undo(entry);
        return true;
    }

    private static void Undo(TrashEntry entry)
    {
        Action<string>? restored = null;
        lock (History)
        {
            var index = History.FindIndex(h => h.Entry.Id == entry.Id && h.Entry.TrashRoot == entry.TrashRoot);
            if (index < 0) return;
            restored = History[index].Restored;
            History.RemoveAt(index);
        }
        try
        {
            var path = TrashService.Restore(entry);
            restored(path);
            Show(L.F("trash.restored", Path.GetFileName(path)), NotificationType.Success);
        }
        catch (TrashException e)
        {
            Show(L.F("trash.restore_failed", Describe(e)), NotificationType.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show(L.F("trash.restore_failed", e.Message), NotificationType.Error);
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
