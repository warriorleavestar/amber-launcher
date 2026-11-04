using System.ComponentModel;
using System.Runtime.CompilerServices;
using Amber.App.Localization;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>换语言的通知：下拉框选项跟着换文字，绑定被丢掉后能被回收（不会因为静态事件一直留在内存里）。</summary>
public class LanguageListenerTests
{
    private sealed class Recorder : IObserver<string>
    {
        public readonly List<string> Values = [];
        public void OnNext(string value) => Values.Add(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // 不切换全局语言（别的测试在并行跑、按中文断言），直接触发“换语言”的通知。
    [Fact]
    public void OptionItemRaisesLabelChanged()
    {
        var item = OptionItem.Key("common.all");
        var changed = 0;
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OptionItem.Label)) changed++; };
        Assert.Equal(L.T("common.all"), item.Label);
        LanguageListeners.Notify();
        Assert.Equal(1, changed);
        Assert.Equal("日本語", OptionItem.Fixed("日本語").Label);
        GC.KeepAlive(item);
    }

    [Fact]
    public void LocalizedTextPushesAndUnsubscribes()
    {
        var recorder = new Recorder();
        var subscription = new LocalizedText("common.all").Subscribe(recorder);
        Assert.Single(recorder.Values);
        LanguageListeners.Notify();
        Assert.Equal(2, recorder.Values.Count);
        subscription.Dispose();
        LanguageListeners.Notify();
        Assert.Equal(2, recorder.Values.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubscribeAndDrop(int count)
    {
        for (var i = 0; i < count; i++) _ = new LocalizedText("common.all").Subscribe(new Recorder());
    }

    [Fact]
    public void DroppedBindingsAreCollected()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = LanguageListeners.LiveCount;
        SubscribeAndDrop(500);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(LanguageListeners.LiveCount < baseline + 50, $"还活着 {LanguageListeners.LiveCount}，开始时 {baseline}");
    }
}
