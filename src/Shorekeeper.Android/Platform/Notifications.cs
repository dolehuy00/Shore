using Android.Content;
using Android.Graphics.Drawables;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine.Transfers;
using AndroidResource = Android.Resource;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android.Platform;

/// <summary>The app's notifications: someone sent files ([Tải] [Bỏ qua]) and the ongoing one of <see cref="TransferService"/>.</summary>
internal static class Notifications
{
    private const string OffersChannel = "offers";
    private const string TransfersChannel = "transfers";
    private const int OfferNotificationId = 2;

    public static void CreateChannels(Context context)
    {
        NotificationManager manager = Manager(context);
        manager.CreateNotificationChannel(new NotificationChannel(OffersChannel, L.T("File gửi đến"), NotificationImportance.High));
        manager.CreateNotificationChannel(new NotificationChannel(TransfersChannel, L.T("Đang truyền file"), NotificationImportance.Low));
    }

    public static Notification Transfer(Context context, string text) =>
        new Notification.Builder(context, TransfersChannel)
            .SetContentTitle("Shorekeeper")!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Drawable.ic_stat_shorekeeper)!
            .SetOngoing(true)!
            .SetContentIntent(OpenApp(context, tab: 0))!
            .Build()!;

    /// <summary>"Huy gửi cho bạn · app.zip · 2,4 GB" with [Tải] [Bỏ qua] (docs/10-ux.md §4).</summary>
    public static void ShowOffer(Context context, ReceivedOffer offer, string senderName)
    {
        var key = new OfferKey(offer.SenderId, offer.OfferId);
        string summary = $"{TransferText.Summary(offer.Files.Select(f => f.RelativePath))} · {TransferText.Size(offer.TotalBytes)}";
        Notification notification = new Notification.Builder(context, OffersChannel)
            .SetContentTitle(L.F("{0} gửi cho bạn", senderName))!
            .SetContentText(summary)!
            .SetStyle(new Notification.BigTextStyle().BigText(offer.Note is null ? summary : $"{summary}\n\"{offer.Note}\""))!
            .SetSmallIcon(Resource.Drawable.ic_stat_shorekeeper)!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenApp(context, tab: 1))!
            .AddAction(Action(context, L.T("Tải"), TransferService.ActionDownload, key))!
            .AddAction(Action(context, L.T("Bỏ qua"), TransferService.ActionDecline, key))!
            .Build()!;
        Manager(context).Notify(Tag(key), OfferNotificationId, notification);
    }

    public static void CancelOffer(Context context, OfferKey key) => Manager(context).Cancel(Tag(key), OfferNotificationId);

    private static Notification.Action Action(Context context, string title, string action, OfferKey key)
    {
        // The data URI makes each offer's intent distinct, so the system does not reuse another offer's extras.
        Intent intent = new Intent(context, typeof(TransferService))
            .SetAction(action)!
            .SetData(AndroidUri.Parse($"shorekeeper://offer/{key.SenderId.Value}/{key.OfferId}"))!
            .PutExtra(TransferService.ExtraSender, key.SenderId.Value)!
            .PutExtra(TransferService.ExtraOffer, key.OfferId)!;
        PendingIntent pending = PendingIntent.GetForegroundService(context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        return new Notification.Action.Builder(Icon.CreateWithResource(context, AndroidResource.Drawable.StatSysDownload), title, pending).Build()!;
    }

    private static PendingIntent OpenApp(Context context, int tab)
    {
        Intent intent = new Intent(context, typeof(MainActivity)).PutExtra(MainActivity.ExtraTab, tab)!;
        return PendingIntent.GetActivity(context, tab, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private static string Tag(OfferKey key) => $"{key.SenderId.Value}/{key.OfferId}";

    private static NotificationManager Manager(Context context) => (NotificationManager)context.GetSystemService(Context.NotificationService)!;
}
