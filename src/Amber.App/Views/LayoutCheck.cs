using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 开发用的界面尺寸检查：把窗口内容按不同的逻辑尺寸排版（对应常见屏幕和缩放下窗口能有的大小），
/// 每个页面截一张图（按缩放倍数渲染），并找出被切掉的东西：
///   文字比自己的宽度长却既不换行也不省略；控件超出窗口右边或下边，又不在可以滚动的区域里。
/// AMBER_LAYOUTCHECK=报告目录 时单独跑；界面自检也会先跑一遍。
/// </summary>
internal sealed class LayoutCheck(MainWindow window, MainViewModel main, string reportDir)
{
    /// <summary>
    /// 逻辑尺寸（扣掉任务栏后的可用区域）：最小窗口；1280×720 开 150%；1920×1080 开 200%；1366×768 开 125%；
    /// 1920×1080 开 150%；2560×1440 开 175%。
    /// </summary>
    public static readonly (double Width, double Height, string Name)[] Sizes =
    [
        (760, 440, "最小窗口"),
        (853, 448, "1280x720@150%"),
        (960, 500, "1920x1080@200%"),
        (1093, 580, "1366x768@125%"),
        (1280, 690, "1920x1080@150%"),
        (1463, 790, "2560x1440@175%"),
    ];

    public static readonly double[] Scales = [1.0, 1.5, 2.0];

    public sealed record Problem(string Page, string Size, string What);

    public async Task<List<Problem>> RunAsync(Action<string>? log = null)
    {
        Directory.CreateDirectory(reportDir);
        var problems = new List<Problem>();
        if (window.Content is not Control root) return problems;
        var original = main.CurrentPage;
        var originalLanguage = Amber.Core.Localization.L.Language;
        // 每种语言都排一遍：英文、日文的字更长，最容易被切。
        foreach (var (language, _) in Amber.Core.Localization.L.Languages)
        {
        Amber.Core.Localization.L.SetLanguage(language);
        await Task.Delay(300);
        foreach (var page in main.Pages)
        {
            main.CurrentPage = page;
            await Task.Delay(600);
            foreach (var (w, h, name) in Sizes)
            {
                // 真的把窗口调成这个大小（比屏幕大也行，Windows 允许程序设置），让窗口自己排版。
                window.MinWidth = 0;
                window.MinHeight = 0;
                window.Width = w;
                window.Height = h;
                await Task.Delay(250);
                var found = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window.UpdateLayout();
                    var size = root.Bounds.Size;
                    var list = Find(root, size).Select(p => new Problem($"{page.Title}[{language}]", name, p)).ToList();
                    // 比这台电脑屏幕还大的尺寸 Windows 不让设，这种尺寸跳过（不算问题）。
                    if (Math.Abs(size.Width - w) > 1 || Math.Abs(size.Height - h) > 1)
                    {
                        log?.Invoke($"  跳过 {page.Title} @ {name}：这台电脑的屏幕放不下，窗口实际 {size.Width:F0}×{size.Height:F0}");
                        return [];
                    }
                    // 截图：最小和用户那种小屏幕按 1.5 倍渲染，最大的按 2 倍，其余 1 倍，够看清字有没有被切。
                    var scale = name switch { "最小窗口" => 1.0, "1280x720@150%" => 1.5, "2560x1440@175%" => 2.0, _ => 1.0 };
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)(w * scale), (int)(h * scale)), new Vector(96 * scale, 96 * scale));
                    bitmap.Render(root);
                    bitmap.Save(Path.Combine(reportDir, $"{language}-{main.Pages.IndexOf(page) + 1}-{name.Replace('%', 'p')}.png"));
                    return list;
                });
                problems.AddRange(found);
                foreach (var p in found.Take(5)) log?.Invoke($"  {p.Page} @ {p.Size}: {p.What}");
            }
        }
        }
        Amber.Core.Localization.L.SetLanguage(originalLanguage);
        main.CurrentPage = original;
        window.MinWidth = MainWindow.PreferredMinWidth;
        window.MinHeight = MainWindow.PreferredMinHeight;
        File.WriteAllLines(Path.Combine(reportDir, "layout.txt"), problems.Select(p => $"{p.Page} @ {p.Size}: {p.What}"));
        return problems;
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
        return result.Distinct().ToList();
    }

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
