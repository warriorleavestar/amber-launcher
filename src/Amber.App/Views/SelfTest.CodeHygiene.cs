using Amber.App.Services;
using Amber.App.ViewModels;
using Avalonia.Controls;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>粘贴框：PasswordChar 打码，默认不显示原文（联机页空闲时看）。</summary>
    private async Task PasteBoxMaskedAsync(MultiplayerViewModel host)
    {
        var join = await FindAsync<TextBox>(t => t.PasswordChar == '•' && t.Text == host.JoinCode, 5);
        Check("码：粘贴框默认打码，可以点“显示”", join is not null && Avalonia.Threading.Dispatcher.UIThread.Invoke(() => !join.RevealPassword), host.JoinCode.Length.ToString());
        if (join is null) return;
        host.ToggleJoinCodeShownCommand.Execute(null);
        await Ui.FlushAsync();
        var revealed = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => join.RevealPassword);
        host.ToggleJoinCodeShownCommand.Execute(null);
        Check("码：点“显示”后粘贴框显示原文", revealed);
    }

    /// <summary>
    /// 界面上的码：待用邀请默认打码；复制后经真的剪贴板（Avalonia）按时清掉，剪贴板换了内容就不动；
    /// 切到别的页面再回来，所有码重新打码；数据封装每种模式有一句说明。
    /// 自检会动剪贴板：开始前记下原来的文字，结束后放回去。
    /// </summary>
    private async Task CodeHygieneAsync(MultiplayerViewModel host, string invite)
    {
        var item = host.PendingInvites.FirstOrDefault();
        Check("码：待用邀请列表默认打码", item is not null && !item.Label.Contains(invite, StringComparison.Ordinal), item?.Label ?? "");
        if (item is not null)
        {
            host.TogglePendingInviteShownCommand.Execute(item);
            Check("码：待用邀请可以点“显示”看完整的码", item.Label.Contains(invite, StringComparison.Ordinal));
        }

        // 切到别的页面再回来：都重新打码。
        host.ToggleInviteShownCommand.Execute(null);
        host.ToggleJoinCodeShownCommand.Execute(null);
        main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
        await Ui.FlushAsync();
        main.CurrentPage = host;
        await Ui.FlushAsync();
        Check("码：离开联机页后重新打码", !host.InviteShown && !host.JoinCodeShown && host.PendingInvites.All(i => !i.Shown)
            && host.InviteDisplay != invite, host.InviteDisplay);

        // 剪贴板：真的 Avalonia 剪贴板，把 60 秒缩短成 2 秒。
        var original = AvaloniaClipboard.FromMainWindow() is { } board ? await board.GetTextAsync() : null;
        var saved = host.SecretClipboard;
        try
        {
            host.SecretClipboard = new SensitiveClipboard(AvaloniaClipboard.FromMainWindow, (_, ct) => Task.Delay(TimeSpan.FromSeconds(2), ct));
            await host.CopyInviteCommand.ExecuteAsync(null);
            var copied = await ClipboardTextAsync();
            Check("码：复制邀请码，状态栏提示会自动清除", copied == invite && host.Status.Contains("60 秒"), host.Status);
            // 提示写在邀请对话框里（邀请码和复制按钮下面）：打开对话框看。
            host.OpenInviteDialogCommand.Execute(null);
            Check("码：邀请码旁边写着“60 秒后自动从剪贴板清除”",
                await FindAsync<TextBlock>(t => t.Text == "60 秒后自动从剪贴板清除", 5) is not null);
            host.CloseInviteDialogCommand.Execute(null);
            await host.SecretClipboard.PendingClears;
            var after = await ClipboardTextAsync();
            Check("码：到点后剪贴板里的邀请码被清掉", string.IsNullOrEmpty(after), after ?? "");

            await host.CopyInviteCommand.ExecuteAsync(null);
            if (AvaloniaClipboard.FromMainWindow() is { } other) await other.SetTextAsync("自检：用户自己复制的文字");
            await host.SecretClipboard.PendingClears;
            var kept = await ClipboardTextAsync();
            Check("码：剪贴板换成别的内容时不清", kept == "自检：用户自己复制的文字", kept ?? "");
        }
        finally
        {
            host.SecretClipboard = saved;
            if (AvaloniaClipboard.FromMainWindow() is { } restore)
            {
                if (original is not null) await restore.SetTextAsync(original);
                else await restore.ClearAsync();
            }
        }

        // 数据封装：每种模式一句“谁看得到游戏流量”。
        var privacy = host.TransportChoices.Select(c => c.Privacy).ToList();
        Check("码：数据封装每种模式都有一句说明", privacy.All(p => p.Length > 10) && privacy.Skip(1).All(p => p != privacy[0]), string.Join(" | ", privacy.Distinct()));
    }

    private static async Task<string?> ClipboardTextAsync() =>
        AvaloniaClipboard.FromMainWindow() is { } board ? await board.GetTextAsync() : null;
}
