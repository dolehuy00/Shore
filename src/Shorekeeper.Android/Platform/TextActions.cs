using Android.Content;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.Engine.Transfers;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Text in "Gửi văn bản": what is copied starts the text to send; received text can be copied, or opened when it is a
/// web address.
/// </summary>
internal static class TextActions
{
    /// <summary>
    /// The text copied on the phone, to start "Gửi văn bản…" with (copy, open Shorekeeper, send); empty when nothing
    /// or no plain text is copied, or it is too long to send. Android shows that the app read the clipboard.
    /// </summary>
    public static string Copied(Context context)
    {
        var clipboard = (ClipboardManager)context.GetSystemService(Context.ClipboardService)!;
        string text = clipboard.PrimaryClip is { ItemCount: > 0 } clip ? clip.GetItemAt(0)?.Text ?? "" : "";
        return text.Length <= OfferService.MaxNoteLength && !string.IsNullOrWhiteSpace(text) ? text : "";
    }

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
