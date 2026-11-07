using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Amber.Android;

[Application]
public sealed class Application : AvaloniaAndroidApplication<Mobile.App>
{
    public Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
