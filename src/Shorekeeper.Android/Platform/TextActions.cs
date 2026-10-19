using Android.Content;
using Shorekeeper.Desktop.ViewModels;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android.Platform;

/// <summary>What can be done with received text ("Gửi văn bản"): copy it, or open it when it is a web address.</summary>
internal static class TextActions
{
    public static void Copy(Context context, string text)
    {
        var clipboard = (ClipboardManager)context.GetSystemService(Context.ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText("Shorekeeper", text);
    }

    /// <summary>The browser (or the app that handles the address) for a web link; null for anything else.</summary>
    public static Intent? ViewIntent(string text) =>
        TransferText.LinkOf(text) is { } link
            ? new Intent(Intent.ActionView, AndroidUri.Parse(link.AbsoluteUri)).AddFlags(ActivityFlags.NewTask)
            : null;

    public static void Open(Context context, string text)
    {
        if (ViewIntent(text) is { } intent)
        {
            try
            {
                context.StartActivity(intent);
            }
            catch (ActivityNotFoundException)
            {
                // No browser: the text stays on the card to copy.
            }
        }
    }
}
