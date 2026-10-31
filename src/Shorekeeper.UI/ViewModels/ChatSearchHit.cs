using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// A message found by its words (docs/18-chat-extras.md §3): where it is, and the words around what matched.
/// </summary>
/// <param name="Conversation">The person's or the group's name.</param>
/// <param name="Snippet">"Mai: …nhớ mang báo cáo…", the author first.</param>
/// <param name="EntryId">The entry of <see cref="ChatViewModel.Messages"/> it is: the message's id, or "sent:"/"received:" and the offer's.</param>
public sealed partial class ChatSearchHit(
    string conversation, string snippet, DateTimeOffset at, DeviceId? peer, string? groupId, string entryId, ChatViewModel owner)
{
    public string Conversation => conversation;

    public string Snippet => snippet;

    public DateTimeOffset At => at;

    /// <summary>"14:05", "Hôm qua", "20/10".</summary>
    public string TimeText => ChatViewModel.ShortTime(at);

    public DeviceId? Peer => peer;

    public string? GroupId => groupId;

    public bool IsGroup => groupId is not null;

    public string EntryId => entryId;

    [RelayCommand]
    private void Open() => owner.OpenAt(this);
}
