using Amber.Core.Localization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

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

/// <summary>一个键的文字：订阅时先给当前值，换语言时再给新值。</summary>
public sealed class LocalizedText(string key) : IObservable<string>
{
    public IDisposable Subscribe(IObserver<string> observer)
    {
        observer.OnNext(L.T(key));
        void Changed() => observer.OnNext(L.T(key));
        L.Changed += Changed;
        return new Unsubscriber(() => L.Changed -= Changed);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
