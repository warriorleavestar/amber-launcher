using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Amber.App.Views;

/// <summary>
/// 删除实例这类不能撤销的操作用的确认对话框：写明删什么、多大、无法撤销。默认焦点在“取消”上，Esc 取消。
/// 大小在后台算，算出来之前显示“正在计算…”，算好之前也可以确认。
/// </summary>
public sealed class ConfirmDialog : Window
{
    private readonly TextBlock _body;

    private ConfirmDialog(string title, string body, string confirm)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MaxWidth = 520;
        _body = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        var cancel = new Button { Content = L.T("common.cancel"), Classes = { "secondary" }, IsCancel = true };
        var ok = new Button { Content = confirm, Classes = { "danger", "armed" } };
        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" }, Margin = new Thickness(0) },
                _body,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
        };
    }

    /// <summary>显示对话框；bodyLater 不为 null 时在后台算好后替换正文（比如带上占用空间）。返回用户是否确认。</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string body, string confirm, Func<Task<string>>? bodyLater = null)
    {
        var dialog = new ConfirmDialog(title, body, confirm);
        if (bodyLater is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await bodyLater();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => dialog._body.Text = text);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            });
        return await dialog.ShowDialog<bool>(owner);
    }
}
