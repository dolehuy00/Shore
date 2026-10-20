using Android.Content;
using Android.Gms.Tasks;
using Shorekeeper.Core.Localization;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;
using JavaObject = Java.Lang.Object;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// "Quét mã QR": Google's code scanner, run by Google Play services in its own screen. The app needs no camera
/// permission and sees nothing but the code's text. The first scan may download the scanner.
/// </summary>
internal static class QrScanner
{
    /// <returns>The text of the QR code, or null when the user went back.</returns>
    /// <exception cref="InvalidOperationException">With the reason to show, e.g. no Google Play services.</exception>
    public static Task<string?> ScanAsync(Context context)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            GmsBarcodeScannerOptions options = new GmsBarcodeScannerOptions.Builder().SetBarcodeFormats(Barcode.FormatQrCode).Build();
            var listener = new Listener(done);
            GmsBarcodeScanning.GetClient(context, options).StartScan()
                .AddOnSuccessListener(listener)
                .AddOnFailureListener(listener)
                .AddOnCanceledListener(listener);
        }
        catch (Java.Lang.Throwable ex)
        {
            done.TrySetException(Failed(ex));
        }

        return done.Task;
    }

    private static InvalidOperationException Failed(Exception reason) =>
        new(L.T("Không mở được trình quét mã QR. Điện thoại cần Google Play services; lần đầu có thể phải chờ tải trình quét rồi thử lại."), reason);

    private sealed class Listener(TaskCompletionSource<string?> done) : JavaObject, IOnSuccessListener, IOnFailureListener, IOnCanceledListener
    {
        public void OnSuccess(JavaObject? result) => done.TrySetResult((result as Barcode)?.RawValue);

        public void OnFailure(Java.Lang.Exception e) => done.TrySetException(Failed(e));

        public void OnCanceled() => done.TrySetResult(null);
    }
}
