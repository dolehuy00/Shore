namespace Shorekeeper.Core.Platform;

/// <summary>OS notifications with buttons (Windows toast; libnotify / UserNotifications later).</summary>
public interface INotifier
{
    /// <param name="actions">Button captions.</param>
    /// <param name="onChoice">
    /// Called on a background thread with the index of the button pressed, or -1 when the notification itself
    /// is clicked. Not called when it is dismissed.
    /// </param>
    /// <returns>False when the OS cannot show it (unsupported, or notifications turned off for the app): show a window instead.</returns>
    bool TryShow(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions, Action<int> onChoice);
}

public sealed class NullNotifier : INotifier
{
    public static NullNotifier Instance { get; } = new();

    public bool TryShow(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions, Action<int> onChoice) => false;
}
