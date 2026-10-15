using Android.Runtime;
using Avalonia.Android;

namespace A0;

[Application]
public sealed class AndroidApp(IntPtr javaReference, JniHandleOwnership transfer) : AvaloniaAndroidApplication<App>(javaReference, transfer);
