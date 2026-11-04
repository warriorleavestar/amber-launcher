using Amber.Core.Localization;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.Localization;

/// <summary>
/// XAML 里写 {l:T 键}：显示当前语言的文字，换语言时自动更新。
/// 用 IObservable 做绑定源，不靠反射，Native AOT 版本也能用。
/// </summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => new LocalizedText(Key).ToBinding();
}

/// <summary>
/// 换语言的通知：只弱引用着要更新的对象（绑定的观察者、下拉框选项），页面和列表行被丢掉后可以正常回收
/// （Avalonia 控件脱离界面时不会释放绑定，强引用会让每个建过的页面一直留在内存里）。通知总是在界面线程上发。
/// </summary>
public static class LanguageListeners
{
    /// <param name="Notify">不能捕获 Target 本身，否则弱引用就没用了。</param>
    private readonly record struct Entry(WeakReference<object> Target, Action<object> Notify);

    private static readonly List<Entry> Items = [];
    private static readonly Lock Gate = new();
    private static int _addsSincePrune;

    static LanguageListeners() => L.Changed += () =>
    {
        if (Dispatcher.UIThread.CheckAccess()) Notify();
        else Dispatcher.UIThread.Post(Notify);
    };

    public static void Add(object target, Action<object> notify)
    {
        lock (Gate)
        {
            // 每加一批就顺手清掉已经回收的，列表不会一直变长。
            if (++_addsSincePrune >= 256)
            {
                _addsSincePrune = 0;
                Items.RemoveAll(e => !e.Target.TryGetTarget(out _));
            }
            Items.Add(new Entry(new WeakReference<object>(target), notify));
        }
    }

    public static void Remove(object target)
    {
        lock (Gate) Items.RemoveAll(e => !e.Target.TryGetTarget(out var t) || ReferenceEquals(t, target));
    }

    /// <summary>测试用：还活着的订阅有多少。</summary>
    public static int LiveCount
    {
        get
        {
            lock (Gate)
            {
                Items.RemoveAll(e => !e.Target.TryGetTarget(out _));
                return Items.Count;
            }
        }
    }

    /// <summary>让所有订阅重新取一次文字（换语言时自动调用；测试也用它，不用真的切换全局语言）。</summary>
    public static void Notify()
    {
        List<(object Target, Action<object> Notify)> live = [];
        lock (Gate)
        {
            Items.RemoveAll(e => !e.Target.TryGetTarget(out _));
            foreach (var e in Items)
                if (e.Target.TryGetTarget(out var t)) live.Add((t, e.Notify));
        }
        foreach (var (t, notify) in live) notify(t);
    }
}

/// <summary>一个键的文字：订阅时先给当前值，换语言时再给新值。</summary>
public sealed class LocalizedText(string key) : IObservable<string>
{
    public IDisposable Subscribe(IObserver<string> observer)
    {
        observer.OnNext(L.T(key));
        // 弱引用着观察者（绑定本身）：控件和绑定被回收后自动失效。
        var k = key;
        LanguageListeners.Add(observer, o => ((IObserver<string>)o).OnNext(L.T(k)));
        return new Unsubscriber(observer);
    }

    private sealed class Unsubscriber(object observer) : IDisposable
    {
        public void Dispose() => LanguageListeners.Remove(observer);
    }
}

/// <summary>
/// 下拉框的一个选项：列表本身不变（选中的序号不会因为换语言被清掉），只有显示的文字跟着语言换。
/// 显示模板在 App.axaml 里统一定义。
/// </summary>
public sealed class OptionItem : ObservableObject
{
    private readonly string? _key;
    private readonly string? _text;

    private OptionItem(string? key, string? text)
    {
        _key = key;
        _text = text;
        if (key is not null) LanguageListeners.Add(this, o => ((OptionItem)o).OnPropertyChanged(nameof(Label)));
    }

    /// <summary>按翻译键显示。</summary>
    public static OptionItem Key(string key) => new(key, null);

    /// <summary>不翻译的固定文字（比如每种语言自己的名字）。</summary>
    public static OptionItem Fixed(string text) => new(null, text);

    public static OptionItem[] Keys(params string[] keys) => [.. keys.Select(Key)];

    public string Label => _key is null ? _text! : L.T(_key);

    public override string ToString() => Label;
}
