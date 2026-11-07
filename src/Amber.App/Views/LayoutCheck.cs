using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 开发用的界面尺寸检查：把窗口内容按一组逻辑尺寸（从最小窗口到大屏、超宽矮屏）排版，每种语言（加伪本地化）每个页面都排一遍，
/// 找出两类问题：
///   切边：文字比自己的宽度长却既不换行也不省略；控件超出右边或下边，又不在可以滚动的区域里。
///   自适应：出现横向滚动；主要按钮（Classes 含 primary）要滚动才看得到；宽窗口下内容只用了一小半宽度却要竖着滚动。
/// 排版不靠改窗口大小（屏幕比尺寸小时 Windows 不让设），而是直接给内容根元素定宽高，所以任何一台电脑上都能跑完整个矩阵。
/// AMBER_LAYOUTCHECK=报告目录 时单独跑；界面自检也会先跑一遍。
/// AMBER_LAYOUTCHECK_SHOTS=none / some（默认）/ all 控制截多少图。
/// </summary>
internal sealed class LayoutCheck(MainWindow window, MainViewModel main, string reportDir)
{
    /// <summary>
    /// 窗口客户区的逻辑尺寸（DIP）。不对应哪一台电脑：同一个逻辑尺寸在不同分辨率和缩放下都会出现，
    /// 比如 1280×800 既是 1920×1200 开 150%，也是 2560×1600 开 200%。
    /// </summary>
    public static readonly (double Width, double Height, string Name)[] Sizes =
    [
        (760, 440, "760x440-min"),
        (853, 402, "853x402"),
        (1024, 640, "1024x640"),
        (1280, 800, "1280x800"),
        (1600, 900, "1600x900"),
        (1920, 1080, "1920x1080"),
        (2560, 1440, "2560x1440"),
        (2560, 900, "2560x900-wide-short"),
    ];

    /// <summary>截图按这些缩放渲染。排版本身是逻辑像素，缩放只改截图的像素密度（取整按这台电脑的缩放算）。</summary>
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>小、中、大三档：批次报告里的截图用这三种。</summary>
    public static readonly string[] ReportSizes = ["853x402", "1280x800", "1920x1080"];

    /// <summary>单行输入框最宽多少算合理（路径、命令这类长值也够用，再宽就该限宽）。</summary>
    public const double MaxLineInput = 720;

    /// <summary>说明文字一行最长多少（约 45 个汉字 / 90 个英文字母，规格 6.1 的说明段落 MaxWidth=640 留了余量）。</summary>
    public const double MaxLineText = 760;

    public enum Kind { Clipped, Adaptive }

    public sealed record Problem(string Page, string Size, string What, Kind Kind = Kind.Clipped);

    /// <summary>四种语言，再加一遍伪本地化（英文加长 40%，最容易撑破布局）。</summary>
    private static IEnumerable<(string Code, bool Pseudo, string Tag)> Variants() =>
        Amber.Core.Localization.L.Languages.Select(l => (l.Code, false, l.Code)).Append(("en", true, "pseudo"));

    private static string ShotMode => Environment.GetEnvironmentVariable("AMBER_LAYOUTCHECK_SHOTS") is { Length: > 0 } m ? m : "some";

    public async Task<List<Problem>> RunAsync(Action<string>? log = null)
    {
        Directory.CreateDirectory(reportDir);
        var problems = new List<Problem>();
        if (window.Content is not Control root) return problems;
        var original = main.CurrentPage;
        var originalLanguage = Amber.Core.Localization.L.Language;
        var originalPseudo = Amber.Core.Localization.L.Pseudo;
        var mode = ShotMode;
        try
        {
            foreach (var (code, pseudo, tag) in Variants())
            {
                Amber.Core.Localization.L.Pseudo = pseudo;
                Amber.Core.Localization.L.SetLanguage(code);
                await Task.Delay(300);
                foreach (var page in main.Pages)
                {
                    main.CurrentPage = page;
                    await Task.Delay(500);
                    var index = main.Pages.IndexOf(page) + 1;
                    foreach (var (w, h, name) in Sizes)
                    {
                        var found = await CheckAtAsync(root, w, h, $"{page.Title}[{tag}]", name, () =>
                        {
                            if (mode == "none") return;
                            // some：简体中文和伪本地化每种尺寸一张（1 倍）；简体中文的小、中、大三档再按每种缩放各一张。
                            var shoot = mode == "all" || tag is "zh-Hans" or "pseudo";
                            if (shoot) Shot(root, w, h, 1.0, $"{tag}-{index}-{name}");
                            if ((mode == "all" || tag == "zh-Hans") && ReportSizes.Contains(name))
                                foreach (var scale in Scales.Where(s => s != 1.0))
                                    Shot(root, w, h, scale, $"{tag}-{index}-{name}@{scale * 100:F0}");
                        });
                        problems.AddRange(found);
                        foreach (var p in found.Take(3)) log?.Invoke($"  {p.Page} @ {p.Size}: {p.What}");
                    }
                }
            }
        }
        finally
        {
            Release(root);
            Amber.Core.Localization.L.Pseudo = originalPseudo;
            Amber.Core.Localization.L.SetLanguage(originalLanguage);
            main.CurrentPage = original;
        }
        WriteReport(problems);
        return problems;
    }

    /// <summary>
    /// 只检查现在显示的页面（保持它当前的状态：填好内容的实例、正在主持或已加入的房间），四种语言 × 小、中、大三档。
    /// 截图名带上 tag。
    /// </summary>
    public async Task<List<Problem>> CheckCurrentAsync(string tag, Action<string>? log = null)
    {
        Directory.CreateDirectory(reportDir);
        var problems = new List<Problem>();
        if (window.Content is not Control root) return problems;
        var originalLanguage = Amber.Core.Localization.L.Language;
        try
        {
            foreach (var (language, _) in Amber.Core.Localization.L.Languages)
            {
                Amber.Core.Localization.L.SetLanguage(language);
                await Task.Delay(300);
                foreach (var (w, h, name) in Sizes.Where(s => s.Name == "760x440-min" || ReportSizes.Contains(s.Name)))
                {
                    var found = await CheckAtAsync(root, w, h, $"{tag}[{language}]", name,
                        () => Shot(root, w, h, 1.5, $"{tag}-{language}-{name}"));
                    problems.AddRange(found);
                    foreach (var p in found.Take(3)) log?.Invoke($"  {p.Page} @ {p.Size}: {p.What}");
                }
            }
        }
        finally
        {
            Release(root);
            Amber.Core.Localization.L.SetLanguage(originalLanguage);
        }
        return problems;
    }

    /// <summary>把内容根元素定成 w×h（左上对齐），等排版稳定后检查，再截图。</summary>
    private static async Task<List<Problem>> CheckAtAsync(Control root, double w, double h, string page, string name, Action shoot)
    {
        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.VerticalAlignment = VerticalAlignment.Top;
        root.Width = w;
        root.Height = h;
        await Task.Delay(200);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            root.UpdateLayout();
            var size = root.Bounds.Size;
            if (Math.Abs(size.Width - w) > 1 || Math.Abs(size.Height - h) > 1)
                return [new Problem(page, name, $"内容根元素没排成 {w}×{h}（实际 {size.Width:F0}×{size.Height:F0}）", Kind.Adaptive)];
            var list = Find(root, size).Select(p => new Problem(page, name, p)).ToList();
            list.AddRange(FindAdaptive(root, size).Select(p => new Problem(page, name, p, Kind.Adaptive)));
            shoot();
            return list;
        });
    }

    private static void Release(Control root)
    {
        root.Width = double.NaN;
        root.Height = double.NaN;
        root.HorizontalAlignment = HorizontalAlignment.Stretch;
        root.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void Shot(Control root, double w, double h, double scale, string file)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(w * scale), (int)Math.Round(h * scale)), new Vector(96 * scale, 96 * scale));
        // 根元素本身没有背景（背景在窗口上），直接渲染到透明位图上文字会花，先铺一层窗口的背景色。
        var background = (root as Panel)?.Background;
        if (root is Panel panel && panel.Background is null) panel.Background = window.Background ?? Brushes.Black;
        bitmap.Render(root);
        if (root is Panel restore) restore.Background = background;
        bitmap.Save(Path.Combine(reportDir, file.Replace('%', 'p') + ".png"));
    }

    private void WriteReport(List<Problem> problems)
    {
        var lines = new List<string>
        {
            $"尺寸 {Sizes.Length} 种 × 语言 {Variants().Count()} 种（含伪本地化）× 页面 {main.Pages.Count} 个；这台电脑的缩放 {window.RenderScaling:0.##}（排版取整按它算，其它缩放只改截图像素）",
            $"切边 {problems.Count(p => p.Kind == Kind.Clipped)} 处，自适应 {problems.Count(p => p.Kind == Kind.Adaptive)} 处",
        };
        foreach (var group in problems.GroupBy(p => p.Size))
            lines.Add($"  {group.Key}: 切边 {group.Count(p => p.Kind == Kind.Clipped)}，自适应 {group.Count(p => p.Kind == Kind.Adaptive)}");
        lines.Add("");
        lines.AddRange(problems.Select(p => $"[{(p.Kind == Kind.Clipped ? "切边" : "自适应")}] {p.Page} @ {p.Size}: {p.What}"));
        File.WriteAllLines(Path.Combine(reportDir, "layout.txt"), lines);
    }

    /// <summary>找出被切掉的控件，返回说明（同一页同一尺寸下重复的只报一次）。</summary>
    internal static List<string> Find(Control root, Size size)
    {
        var result = new List<string>();
        foreach (var visual in root.GetVisualDescendants().OfType<Control>())
        {
            if (!visual.IsEffectivelyVisible || visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0) continue;
            var (scrollsX, scrollsY) = InScrollable(visual);
            if (visual.TranslatePoint(new Point(0, 0), root) is not { } topLeft) continue;
            var right = topLeft.X + visual.Bounds.Width;
            var bottom = topLeft.Y + visual.Bounds.Height;
            var label = Describe(visual);
            if (label is null) continue;

            if (visual is TextBlock tb && !InsideInput(tb) && tb.TextWrapping == TextWrapping.NoWrap && tb.TextTrimming == TextTrimming.None
                && tb.TextLayout is { } layout && layout.WidthIncludingTrailingWhitespace > tb.Bounds.Width + tb.Padding.Left + tb.Padding.Right + 1)
                result.Add($"文字放不下（不换行也不省略）：{label}");
            if (!scrollsX && right > size.Width + 1 && topLeft.X < size.Width)
                result.Add($"超出窗口右边 {right - size.Width:F0}px：{label}");
            if (!scrollsY && bottom > size.Height + 1 && topLeft.Y < size.Height)
                result.Add($"超出窗口下边 {bottom - size.Height:F0}px：{label}");
        }
        result.AddRange(FindOverlaps(root));
        return result.Distinct().ToList();
    }

    /// <summary>
    /// 叠在一起的控件：Grid 的星号列被挤成 0 宽时，里面的东西不会被裁掉，而是和旁边那列画在同一个位置
    /// （账号页窄窗口下头像、名字、“使用”“移除”叠成一团就是这样）。这种情况既不超出窗口也不触发文字检查，单独找。
    /// 只比较看得见内容的控件；一个控件在另一个里面（按钮和它的文字）不算。
    /// </summary>
    internal static List<string> FindOverlaps(Control root)
    {
        var items = new List<(Control Control, Rect Rect, string Label)>();
        foreach (var c in root.GetVisualDescendants().OfType<Control>())
        {
            if (!c.IsEffectivelyVisible || c.Bounds.Width <= 2 || c.Bounds.Height <= 2 || InsideInput(c)) continue;
            if (c is Image { Source: null }) continue;
            if (Describe(c) is not { } label) continue;
            if (c.TranslatePoint(new Point(0, 0), root) is not { } p) continue;
            // 只算真正画出来的部分：滚动区域、列表这类会裁剪的上级之外的部分看不见，不算重叠（比如滚到底栏下面的内容）。
            var rect = new Rect(p, c.Bounds.Size);
            foreach (var clip in c.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
                if (clip.TranslatePoint(new Point(0, 0), root) is { } q)
                    rect = rect.Intersect(new Rect(q, clip.Bounds.Size));
            if (rect.Width <= 2 || rect.Height <= 2) continue;
            items.Add((c, rect, label));
        }
        var result = new List<string>();
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
            {
                var (a, b) = (items[i], items[j]);
                var overlap = a.Rect.Intersect(b.Rect);
                if (overlap.Width <= 2 || overlap.Height <= 2) continue;
                if (a.Control.IsVisualAncestorOf(b.Control) || b.Control.IsVisualAncestorOf(a.Control)) continue;
                result.Add($"控件叠在一起：{a.Label} 和 {b.Label}");
            }
        return result;
    }

    /// <summary>自适应方面的问题：横向滚动、主要按钮要滚动才看得到、宽窗口下大片空白而内容挤在一边要滚动。</summary>
    internal static List<string> FindAdaptive(Control root, Size size)
    {
        var result = new List<string>();
        var scrollers = root.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width > 0 && !InsideInput(s) && s.TemplatedParent is not (TextBox or ComboBox))
            .ToList();
        foreach (var sv in scrollers)
            if (sv.Extent.Width > sv.Viewport.Width + 1)
                result.Add($"出现横向滚动（内容宽 {sv.Extent.Width:F0}，可见 {sv.Viewport.Width:F0}）：{FirstText(sv)}");

        foreach (var button in root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible))
        {
            if (button.TranslatePoint(new Point(0, 0), root) is not { } p) continue;
            var visible = p.X >= -1 && p.Y >= -1 && p.X + button.Bounds.Width <= size.Width + 1 && p.Y + button.Bounds.Height <= size.Height + 1;
            // 在滚动区域里时，按钮要落在这个区域当前看得见的部分。
            foreach (var sv in button.GetVisualAncestors().OfType<ScrollViewer>())
                if (sv.TranslatePoint(new Point(0, 0), root) is { } s &&
                    (p.Y + button.Bounds.Height > s.Y + sv.Bounds.Height + 1 || p.X + button.Bounds.Width > s.X + sv.Bounds.Width + 1))
                    visible = false;
            if (!visible) result.Add($"主要按钮要滚动才看得到：{Describe(button) ?? "按钮"}");
        }

        // 宽窗口下的行宽：单行输入框拉得比 MaxLineInput 还宽、能换行的说明文字一行比 MaxLineText 还长，都不好读，应该限宽或分栏。
        foreach (var box in root.GetVisualDescendants().OfType<TextBox>())
            if (box.IsEffectivelyVisible && !box.AcceptsReturn && box.Bounds.Width > MaxLineInput && !InsideInput(box))
                result.Add($"单行输入框太宽（{box.Bounds.Width:F0}px）：{Describe(box)}");
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
            if (text.IsEffectivelyVisible && text.TextWrapping != TextWrapping.NoWrap && text.Bounds.Width > MaxLineText && !InsideInput(text)
                && text.TextLayout is { } layout && layout.Width > MaxLineText)
                result.Add($"说明文字一行太长（{layout.Width:F0}px）：{Describe(text)}");

        // 页面区域：显示当前页面的 ContentControl。宽 1000 以上时，内容的左右范围不到一半、又要竖着滚动，算浪费空间。
        var host = root.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Content is PageViewModel);
        if (host is not null && host.Bounds.Width >= 1000)
        {
            double left = double.MaxValue, right = double.MinValue;
            foreach (var c in host.GetVisualDescendants().OfType<Control>())
            {
                if (!c.IsEffectivelyVisible || Describe(c) is null || c.Bounds.Width <= 0) continue;
                if (c.TranslatePoint(new Point(0, 0), host) is not { } p) continue;
                left = Math.Min(left, p.X);
                right = Math.Max(right, p.X + c.Bounds.Width);
            }
            var used = right > left ? (right - left) / host.Bounds.Width : 1;
            var needsScroll = host.GetVisualDescendants().OfType<ScrollViewer>()
                .Any(s => s.IsEffectivelyVisible && !InsideInput(s) && s.TemplatedParent is not (TextBox or ComboBox) && s.Extent.Height > s.Viewport.Height + 1);
            if (used < 0.5 && needsScroll)
                result.Add($"内容只用了 {used:P0} 的宽度却要竖着滚动（页面宽 {host.Bounds.Width:F0}）");
        }
        return result.Distinct().ToList();
    }

    private static string FirstText(Visual v) =>
        v.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text is { Length: > 0 }) is { } t ? Describe(t) ?? "" : "（无文字）";

    /// <summary>下拉框、输入框里的文字（占位提示、选中项）由控件自己裁剪，不算。</summary>
    private static bool InsideInput(Visual v) => v.GetVisualAncestors().Any(a => a is ComboBox or TextBox);

    /// <summary>这个控件在不在能滚动的区域（ScrollViewer）里，分横向和纵向。</summary>
    private static (bool X, bool Y) InScrollable(Visual visual)
    {
        bool x = false, y = false;
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is ScrollContentPresenter presenter)
            {
                if (presenter.CanHorizontallyScroll) x = true;
                if (presenter.CanVerticallyScroll) y = true;
            }
            // 下拉框、文本框自己会滚动或省略内容，里面的不算。
            if (ancestor is TextBox or ComboBox) return (true, true);
        }
        return (x, y);
    }

    /// <summary>只检查用户看得到内容的控件：文字、按钮、输入框、图片。</summary>
    private static string? Describe(Control c) => c switch
    {
        TextBlock { Text: { Length: > 0 } t } => $"“{(t.Length > 40 ? t[..40] + "…" : t)}”",
        CheckBox { Content: string t } => $"勾选框“{t}”",
        Button { Content: string t } => $"按钮“{t}”",
        TextBox t => $"输入框（{t.PlaceholderText ?? t.Name ?? "无名"}）",
        ComboBox => "下拉框",
        Image => "图片",
        _ => null,
    };
}
