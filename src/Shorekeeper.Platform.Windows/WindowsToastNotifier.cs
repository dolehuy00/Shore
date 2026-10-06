using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shorekeeper.Core.Platform;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Windows toasts for an app without package identity (docs/decisions.md ADR-012): the app id is registered under
/// HKCU\Software\Classes\AppUserModelId, and clicks reach us through the <see cref="ToastNotification.Activated"/>
/// event, which works while Shorekeeper runs. Toasts left in the Action Center are cleared on exit, as nothing
/// could handle a click on them then.
/// </summary>
public sealed class WindowsToastNotifier(ILogger<WindowsToastNotifier> logger) : INotifier, IDisposable
{
    public const string AppId = "Shorekeeper";

    // Toasts stay referenced until they are gone, so their event handlers keep working from the Action Center.
    private readonly ConcurrentDictionary<int, ToastNotification> shown = new();
    private ToastNotifier? notifier;
    private int nextId;

    public bool TryShow(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions, Action<int> onChoice)
    {
        try
        {
            notifier ??= CreateNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                return false;
            }

            var xml = new XmlDocument();
            xml.LoadXml(BuildXml(title, lines, actions));
            var toast = new ToastNotification(xml);
            int id = Interlocked.Increment(ref nextId);
            toast.Activated += (_, args) =>
            {
                shown.TryRemove(id, out _);
                onChoice(args is ToastActivatedEventArgs activated && int.TryParse(activated.Arguments, CultureInfo.InvariantCulture, out int index) ? index : -1);
            };
            toast.Dismissed += (_, args) =>
            {
                // Timed out = moved to the Action Center, where it can still be clicked.
                if (args.Reason != ToastDismissalReason.TimedOut)
                {
                    shown.TryRemove(id, out _);
                }
            };
            toast.Failed += (_, args) =>
            {
                shown.TryRemove(id, out _);
                logger.LogWarning(args.ErrorCode, "Toast could not be shown");
            };

            shown[id] = toast;
            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toasts are not available");
            return false;
        }
    }

    public void Dispose()
    {
        if (notifier is null)
        {
            return;
        }

        try
        {
            ToastNotificationManager.History.Clear(AppId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not clear toasts");
        }
    }

    /// <summary>The body clicked gives "-1", a button its index.</summary>
    internal static string BuildXml(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions)
    {
        var toast = new XElement("toast",
            new XAttribute("launch", "-1"),
            new XElement("visual",
                new XElement("binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title),
                    // A toast shows at most three lines of text, the title included.
                    lines.Take(2).Select(line => new XElement("text", line)))));
        if (actions.Count > 0)
        {
            toast.Add(new XElement("actions",
                actions.Take(5).Select((caption, index) => new XElement("action",
                    new XAttribute("content", caption),
                    new XAttribute("arguments", index.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("activationType", "foreground")))));
        }

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static ToastNotifier CreateNotifier()
    {
        // Name and icon shown on the toast and in Settings → Notifications.
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
        {
            key.SetValue("DisplayName", "Shorekeeper");
            string icon = Path.Combine(AppContext.BaseDirectory, "shorekeeper.png");
            if (File.Exists(icon))
            {
                key.SetValue("IconUri", icon);
            }
        }

        return ToastNotificationManager.CreateToastNotifier(AppId);
    }
}
