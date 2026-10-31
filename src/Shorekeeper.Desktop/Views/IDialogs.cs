using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Shorekeeper.Core.Identity;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// What the view models ask of the windows (<see cref="DialogService"/>): pickers, questions, popups, the clipboard,
/// and sending with what follows it. Given to them by the container, so a test can stand in for the windows.
/// </summary>
public interface IDialogs
{
    /// <summary>An offer went out: the main window may show "Đã gửi".</summary>
    event EventHandler? OfferSent;

    /// <summary>The user pressed "Xem" on a popup or clicked a toast: the main window shows that page.</summary>
    event EventHandler<NavPage>? PageRequested;

    /// <summary>Text alone was sent to one person or one group: the main window opens that conversation.</summary>
    event EventHandler<ChatTarget>? TextSent;

    /// <summary>"Xem" on text that arrived: the main window comes up with the conversation it is in.</summary>
    event EventHandler<ChatTarget>? ChatRequested;

    Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<(DeviceId Id, string Name)> recipients, string? groupId = null, string? text = null);

    Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<OfferBatch> batches, string? text = null, bool showSent = true);

    Task ConnectAsync(DeviceId target, string name);

    Task ShowPairingQrAsync();

    Task AddPeerAsync();

    void Ask(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions, NavPage page, Action<int> onAction);

    void ShowIncomingMessage(string senderName, string text, Action open);

    void ShowIncomingOffer(ReceivedOffer offer, string senderName);

    void ShowIncoming(IncomingPairing request);

    void CloseIncoming(string requestId);

    Task<IReadOnlyList<string>> PickFilesAsync(Window? owner = null);

    Task<IReadOnlyList<string>> PickFolderAsync(Window? owner = null);

    Task<string?> PickFileAsync(string title, FilePickerFileType type);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, FilePickerFileType type);

    Task<IReadOnlyList<string>?> PickOfferFilesAsync(ReceivedOffer offer);

    Task<IReadOnlyList<PickablePeer>?> PickPeopleAsync(string title, string confirmText, string emptyText, IReadOnlyList<PickablePeer> people);

    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices);

    Task<string?> PromptPasswordAsync(string title, string label, string okText, bool confirm, Func<string, Task<string?>> check);

    Task<string?> PromptAsync(string title, string label, string initialText, string? okText = null, bool multiline = false);

    Task<(string Text, bool Option)?> PromptWithOptionAsync(string title, string label, string initialText, string optionText, bool optionChecked);

    Task ChooseAutoAcceptAsync(string name, long? current, double generalLimitGb, Func<long?, Task> save);

    Task CopyTextAsync(string text);

    Task<bool> CopyImageAsync(string path);

    Task<bool> ConfirmOpenAsync(string path);

    void OpenLink(string text);
}
