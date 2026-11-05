using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>
/// 手写的输入校验：VM 自己判断，SetErrors / ClearErrors 记下每个属性的错误，界面上的输入框按 INotifyDataErrorInfo 显示在字段旁边。
/// 不用 ObservableValidator 和 DataAnnotations：它们靠反射读特性，Native AOT 下不安全。
/// 错误文字是翻译键，显示时才取当前语言的文字（换语言后重新取一次就是新语言）。
/// </summary>
public abstract class ValidatingViewModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<(string Key, object?[] Args)>> _errors = new(StringComparer.Ordinal);

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>这个属性现在有没有错误。</summary>
    public bool HasErrorsFor(string property) => _errors.ContainsKey(property);

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return _errors.Values.SelectMany(list => list).Select(e => Amber.Core.Localization.L.F(e.Key, e.Args)).ToList();
        return _errors.TryGetValue(propertyName, out var list) ? list.Select(e => Amber.Core.Localization.L.F(e.Key, e.Args)).ToList() : Array.Empty<string>();
    }

    /// <summary>设这个属性的错误（一条，翻译键加参数）；替换原来的。</summary>
    protected void SetError(string property, string key, params object?[] args)
    {
        _errors[property] = [(key, args)];
        RaiseErrorsChanged(property);
    }

    protected void ClearErrors(string property)
    {
        if (_errors.Remove(property)) RaiseErrorsChanged(property);
    }

    /// <summary>按条件设或清：valid 为 false 时设错误。返回 valid，方便写成 if (!Validate(...)) return;</summary>
    protected bool Validate(string property, bool valid, string key, params object?[] args)
    {
        if (valid) ClearErrors(property);
        else SetError(property, key, args);
        return valid;
    }

    /// <summary>换语言后让界面重新取一遍错误文字。</summary>
    protected void RefreshErrorTexts()
    {
        foreach (var property in _errors.Keys.ToList()) RaiseErrorsChanged(property);
    }

    private void RaiseErrorsChanged(string property)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        OnPropertyChanged(nameof(HasErrors));
    }
}
