using System.ComponentModel;
using Amber.App.ViewModels;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

public class ValidatingViewModelTests
{
    /// <summary>示例：离线名 3–16 个字母、数字或下划线。</summary>
    private sealed class Sample : ValidatingViewModel
    {
        private string _name = "";

        public string Name
        {
            get => _name;
            set
            {
                SetProperty(ref _name, value);
                Validate(nameof(Name), value.Length is >= 3 and <= 16 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "home.status.launch_failed", value);
            }
        }
    }

    [Fact]
    public void SetsAndClearsErrorsWithNotifications()
    {
        var vm = new Sample();
        var changed = new List<string?>();
        ((INotifyDataErrorInfo)vm).ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Name = "ab";
        Assert.True(vm.HasErrors);
        Assert.True(vm.HasErrorsFor(nameof(Sample.Name)));
        var errors = vm.GetErrors(nameof(Sample.Name)).Cast<string>().ToList();
        Assert.Equal([L.F("home.status.launch_failed", "ab")], errors);
        Assert.Single(vm.GetErrors(null).Cast<string>());

        vm.Name = "Steve_01";
        Assert.False(vm.HasErrors);
        Assert.Empty(vm.GetErrors(nameof(Sample.Name)).Cast<string>());
        Assert.Equal([nameof(Sample.Name), nameof(Sample.Name)], changed);

        // 本来就没有错误时不重复通知。
        vm.Name = "Alex";
        Assert.Equal(2, changed.Count);
    }
}
