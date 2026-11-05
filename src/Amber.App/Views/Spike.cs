using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 开发用：界面改版第 0 批的 Avalonia 12 技术验证（AMBER_SPIKE=报告目录）。每一项在真窗口里试一次，
/// 结果写到 spike.txt（可行 / 换写法 / 删除验收项 + 依据），需要看的截图放在同一个目录。跑完关窗口。
/// AMBER_SPIKE_LIVE=秒数 时最后留一段时间每秒改一次“实时播报”的文字，给外面的 UIA 监听脚本数事件。
/// </summary>
internal sealed class Spike(Window window, string reportDir)
{
    private readonly List<string> _lines = [];

    private void Report(string item, string verdict, string evidence) => _lines.Add($"{item}\t{verdict}\t{evidence}");

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        var original = window.Content;
        try
        {
            await ToastAsync();
            await DropAsync();
            await NumericAsync();
            await TransitionAsync();
            Tray();
            await LiveAsync();
        }
        catch (Exception e)
        {
            _lines.Add("ERROR\t" + e);
        }
        finally
        {
            window.Content = original;
            File.WriteAllLines(Path.Combine(reportDir, "spike.txt"), _lines);
            window.Close();
        }
    }

    private async Task ShotAsync(string name)
    {
        await Task.Delay(400);
        var size = window.ClientSize;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
    }

    /// <summary>带按钮的 Toast：WindowNotificationManager.Show(任意内容) + 内容模板，按钮能点。</summary>
    private async Task ToastAsync()
    {
        window.Content = new Border { Background = Brushes.Transparent };
        var manager = new WindowNotificationManager(window) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        await Task.Delay(300);
        var clicked = false;
        var undo = new Button { Content = "Undo", Name = "SpikeUndo" };
        undo.Click += (_, _) => clicked = true;
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Deleted 3 mods", FontWeight = FontWeight.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { undo, new Button { Content = "View" } } },
            },
        };
        manager.Show(content, NotificationType.Information, TimeSpan.FromSeconds(30));
        await Task.Delay(800);
        var found = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SpikeUndo");
        found?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ShotAsync("toast");
        Report("Toast 自定义内容（带按钮）", found is not null && clicked ? "可行" : "换写法",
            found is null ? "通知层里找不到自定义内容" : $"Show(object) 直接显示控件，按钮在可视树里、点击事件{(clicked ? "能触发" : "没触发")}；截图 toast.png");
    }

    /// <summary>全窗口拖放：DragDrop 事件 + DataTransfer 取文件。用代码构造一次放下事件走完处理流程（真正的系统拖放要人来试）。</summary>
    private async Task DropAsync()
    {
        var target = new Border { Background = Brushes.Transparent };
        DragDrop.SetAllowDrop(target, true);
        string? got = null;
        var overlay = false;
        target.AddHandler(DragDrop.DragEnterEvent, (_, _) => overlay = true);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            got = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.Name;
            overlay = false;
        });
        window.Content = target;
        await Task.Delay(300);
        var file = Path.Combine(reportDir, "drop-test.mrpack");
        await File.WriteAllTextAsync(file, "test");
        var storage = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
        if (storage is null)
        {
            Report("拖放（DataTransfer）", "换写法", "StorageProvider 拿不到测试文件");
            return;
        }
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(storage));
        var enter = new DragEventArgs(DragDrop.DragEnterEvent, data, target, new Point(10, 10), KeyModifiers.None);
        target.RaiseEvent(enter);
        var sawOverlay = overlay;
        var drop = new DragEventArgs(DragDrop.DropEvent, data, target, new Point(10, 10), KeyModifiers.None);
        target.RaiseEvent(drop);
        Report("拖放（DataTransfer）", got == "drop-test.mrpack" && sawOverlay ? "可行" : "换写法",
            $"DragEnter {(sawOverlay ? "触发" : "没触发")}（显示放置高亮层用），Drop 里 DataTransfer.TryGetFiles() 取到 {got ?? "null"}；系统层面的拖放还要人手拖一次确认");
    }

    /// <summary>NumericUpDown + 全角数字转换：输入“１９２０”得到 1920。</summary>
    private async Task NumericAsync()
    {
        var box = new NumericUpDown { Minimum = 0, Maximum = 10000, FormatString = "0", TextConverter = FullWidthDigits.Instance, Width = 200 };
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { box } };
        await Task.Delay(300);
        box.Focus();
        box.Text = "１９２０";
        // 失去焦点时提交。
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { new TextBox() } };
        await Task.Delay(200);
        var withConverter = box.Value;
        var plain = new NumericUpDown { Minimum = 0, Maximum = 10000, FormatString = "0", Width = 200 };
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { plain } };
        await Task.Delay(300);
        plain.Focus();
        plain.Text = "１９２０";
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { new TextBox() } };
        await Task.Delay(200);
        Report("NumericUpDown 全角数字", withConverter == 1920 ? "可行" : "换写法",
            $"带 TextConverter 时输入“１９２０”得到 {withConverter?.ToString() ?? "null"}；不带时得到 {plain.Value?.ToString() ?? "null"}");
    }

    /// <summary>TransitioningContentControl：切页时 View 会重建，Tab 选择要存在 VM 里才能恢复。</summary>
    private async Task TransitionAsync()
    {
        var host = new TransitioningContentControl
        {
            PageTransition = new Avalonia.Animation.CrossFade(TimeSpan.FromMilliseconds(150)),
            ContentTemplate = new FuncDataTemplate<SpikePage>((page, _) =>
            {
                var tabs = new TabControl { Items = { new TabItem { Header = "A" }, new TabItem { Header = "B" }, new TabItem { Header = "C" } } };
                tabs.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, new Avalonia.Data.Binding(nameof(SpikePage.TabIndex)) { Mode = Avalonia.Data.BindingMode.TwoWay });
                return tabs;
            }),
        };
        window.Content = host;
        var first = new SpikePage();
        host.Content = first;
        await Task.Delay(500);
        var viewBefore = host.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (viewBefore is not null) viewBefore.SelectedIndex = 2;
        host.Content = new SpikePage();
        await Task.Delay(500);
        host.Content = first;
        await Task.Delay(500);
        var viewAfter = host.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        var rebuilt = !ReferenceEquals(viewBefore, viewAfter);
        Report("TransitioningContentControl 状态保留", viewAfter?.SelectedIndex == 2 ? "可行（状态放 VM）" : "换写法",
            $"切走再切回来 View {(rebuilt ? "重建了" : "没重建")}；Tab 选择绑在 VM 上，回来后是 {viewAfter?.SelectedIndex}（VM 里是 {first.TabIndex}）");
    }

    private sealed class SpikePage : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private int _tabIndex;
        public int TabIndex { get => _tabIndex; set => SetProperty(ref _tabIndex, value); }
    }

    /// <summary>系统托盘图标：能不能建、能不能显示和收起。</summary>
    private void Tray()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Jay/Assets/jay.ico"));
            var icon = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "Jay", IsVisible = true };
            var icons = new TrayIcons { icon };
            TrayIcon.SetIcons(Application.Current!, icons);
            var shown = icon.IsVisible;
            icon.IsVisible = false;
            TrayIcon.SetIcons(Application.Current!, []);
            icon.Dispose();
            Report("托盘图标（TrayIcon）", shown ? "可行" : "换写法", "TrayIcon 建立、显示、收起都没出错（任务栏里看得见要人看一眼）");
        }
        catch (Exception e)
        {
            Report("托盘图标（TrayIcon）", "删除验收项", e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// 实时播报（LiveSetting=Polite）：Avalonia 12 的 Win32 无障碍层有 RaiseLiveRegionChanged（UIA 事件 20024）。
    /// 这里只把文字放好、按秒改；有没有真的发出事件由外面的 UIA 监听脚本数。
    /// </summary>
    private async Task LiveAsync()
    {
        var text = new TextBlock { Text = "status 0", Margin = new Thickness(20) };
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(text, "status 0");
        window.Content = text;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("AMBER_SPIKE_LIVE"), out var s) ? s : 0;
        for (var i = 1; i <= seconds; i++)
        {
            await Task.Delay(1000);
            text.Text = "status " + i;
            AutomationProperties.SetName(text, "status " + i);
        }
        Report("实时播报（LiveSetting）", "见 UIA 监听结果", $"改了 {seconds} 次文字；有没有发出 LiveRegionChanged（UIA 事件 20024）看 tools/spike-live-check.ps1 的输出");
    }
}

/// <summary>把全角数字（０–９）和全角负号、小数点换成半角，再按普通数字解析。</summary>
internal sealed class FullWidthDigits : Avalonia.Data.Converters.IValueConverter
{
    public static readonly FullWidthDigits Instance = new();

    public static string Normalize(string text)
    {
        var chars = text.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var digit = System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(chars[i]);
            if (digit >= 0) chars[i] = (char)('0' + digit);
            else if (chars[i] == '－') chars[i] = '-';
            else if (chars[i] == '．') chars[i] = '.';
        }
        return new string(chars);
    }

    // NumericUpDown 用 Convert 把文字换成数字（ConvertBack 把数字显示成文字）。
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string s && decimal.TryParse(Normalize(s), System.Globalization.NumberStyles.Number, culture, out var d) ? d : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is decimal d ? d.ToString("0", culture) : value?.ToString();
}
