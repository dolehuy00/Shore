using Android.Runtime;
using Avalonia.Android;

namespace Shorekeeper.Android;

[Application]
public sealed class AndroidApp(IntPtr javaReference, JniHandleOwnership transfer) : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    public override void OnCreate()
    {
        App.Engine = EngineHost.StartAsync(this);
        base.OnCreate();
    }
}
