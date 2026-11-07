using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Amber.App.Views;

public partial class DownloadPage : UserControl
{
    /// <summary>列表一行（版本号、胶囊、日期加安装按钮）至少要 320，加面板最少 260 和间距放不下时，安装面板改成盖在列表上的抽屉（改版规格 4.4 compact）。</summary>
    internal const double ListMinWidth = 320;
    internal const double CompactWidth = ListMinWidth + 260 + 14;

    /// <summary>打开安装面板的那个控件；面板关掉后焦点回到它。</summary>
    private IInputElement? _opener;

    public DownloadPage()
    {
        InitializeComponent();
        VersionList.DoubleTapped += (_, e) => { if (ConfigureSelected()) e.Handled = true; };
        VersionList.KeyDown += (_, e) => { if (e.Key == Key.Enter && ConfigureSelected()) e.Handled = true; };
        InstallPanel.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not DownloadViewModel vm || vm.IsBusy) return;
            vm.CloseConfigureCommand.Execute(null);
            e.Handled = true;
        };
        // 点按钮打开面板前记下焦点所在（按钮本身），面板关掉时还回去。
        AddHandler(Button.ClickEvent, (_, _) => _opener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), RoutingStrategies.Tunnel);
        InstallPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (InstallPanel.IsVisible) return;
            if (_opener is Visual { IsEffectivelyVisible: true } && _opener.Focusable) _opener.Focus();
            _opener = null;
        };
        // 不在布局过程中改列：放到下一轮再按新宽度排。
        BodyGrid.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < CompactWidth;
            var width = e.NewSize.Width;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyCompact(compact, width));
        };
    }

    /// <summary>窄窗时面板盖在列表上（靠右的抽屉），宽屏时回到右侧一列。</summary>
    private void ApplyCompact(bool compact, double width)
    {
        foreach (var panel in new Control[] { InstallPanel, InstalledCard })
        {
            Grid.SetColumn(panel, compact ? 0 : 1);
            Grid.SetColumnSpan(panel, compact ? 2 : 1);
            panel.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
            panel.Margin = compact ? new Thickness(0, 0, 0, 14) : new Thickness(14, 0, 0, 14);
            panel.ZIndex = compact ? 1 : 0;
        }
        InstallPanel.MinWidth = compact ? 0 : 260;
        // 宽屏：260–340，并给列表留够 ListMinWidth；窄窗：抽屉最多 320。
        InstallPanel.Width = compact ? Math.Min(320, Math.Max(200, width - 24)) : Math.Clamp(Math.Min(width * 0.4, width - ListMinWidth - 14), 260, 340);
        InstalledCard.MinWidth = InstallPanel.MinWidth;
        InstalledCard.Width = InstallPanel.Width;
        Classes.Set("compact", compact);
    }

    private bool ConfigureSelected()
    {
        if (DataContext is not DownloadViewModel vm || VersionList.SelectedItem is not RemoteVersionItem item) return false;
        _opener = VersionList;
        vm.ConfigureCommand.Execute(item);
        return true;
    }
}
