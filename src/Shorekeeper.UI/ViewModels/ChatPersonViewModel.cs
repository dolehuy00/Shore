using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.UI.ViewModels;

/// <summary>A row of "Tin nhắn": a contact or a group, with the last message when there is one.</summary>
public sealed partial class ChatPersonViewModel : ObservableObject
{
    private readonly ChatViewModel owner;

    public ChatPersonViewModel(DeviceId peer, ChatViewModel owner)
    {
        Peer = peer;
        this.owner = owner;
    }

    public ChatPersonViewModel(string groupId, ChatViewModel owner)
    {
        GroupId = groupId;
        this.owner = owner;
    }

    /// <summary>The person; empty for a group.</summary>
    public DeviceId Peer { get; }

    /// <summary>A group's conversation (docs/16-group-chat.md §6) rather than one with <see cref="Peer"/>.</summary>
    public string? GroupId { get; }

    public bool IsGroup => GroupId is not null;

    /// <summary>A group whose notifications are off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteText))]
    public partial bool IsMuted { get; set; }

    public string MuteText => IsMuted ? L.T("Bật thông báo") : L.T("Tắt thông báo");

    /// <summary>A group's "12 thành viên · 5 online".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string MembersText { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComputer))]
    public partial bool IsPhone { get; set; }

    /// <summary>A person on a computer: neither a phone nor a group.</summary>
    public bool IsComputer => !IsPhone && !IsGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsBusy { get; set; }

    public string StatusText => IsGroup ? MembersText : !IsOnline ? "Offline" : IsBusy ? L.T("Bận") : "Online";

    /// <summary>"Bạn: được rồi", or "Chưa có tin nhắn".</summary>
    [ObservableProperty]
    public partial string Preview { get; set; } = "";

    [ObservableProperty]
    public partial string TimeText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int Unread { get; set; }

    public bool HasUnread => Unread > 0;

    /// <summary>The conversation open on the right (desktop).</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private void Open() => owner.Open(this);

    /// <param name="file">The newest offer with this person; it shows when newer than the last message.</param>
    internal ChatPersonViewModel Update(ChatConversation? conversation, ChatLastFile? file)
    {
        Unread = conversation?.Unread ?? 0;
        if (file is not null && (conversation is null || file.At > conversation.Last.SentAt))
        {
            TimeText = ChatViewModel.ShortTime(file.At);
            Preview = file.Outgoing ? L.F("Bạn: {0}", TransferText.Preview(file.Text)) : TransferText.Preview(file.Text);
            return this;
        }

        TimeText = conversation is null ? "" : ChatViewModel.ShortTime(conversation.Last.SentAt);
        Preview = conversation is null ? L.T("Chưa có tin nhắn")
            : conversation.Last.Outgoing ? L.F("Bạn: {0}", TransferText.Preview(ChatBubbleViewModel.TextOf(conversation.Last)))
            : TransferText.Preview(ChatBubbleViewModel.TextOf(conversation.Last));
        return this;
    }

    /// <param name="author">"Bạn", or the member who wrote the last message.</param>
    /// <param name="file">The group's newest offer; it shows when newer than the last message, under <paramref name="fileAuthor"/>.</param>
    internal ChatPersonViewModel Update(
        GroupChatConversation? conversation, string? author, bool muted, ChatLastFile? file = null, string? fileAuthor = null)
    {
        IsMuted = muted;
        Unread = conversation?.Unread ?? 0;
        if (conversation is not null)
        {
            Name = conversation.Name;
        }

        if (file is not null && (conversation is null || file.At > conversation.Last.SentAt))
        {
            TimeText = ChatViewModel.ShortTime(file.At);
            Preview = L.F("{0}: {1}", fileAuthor, TransferText.Preview(file.Text));
            return this;
        }

        TimeText = conversation is null ? "" : ChatViewModel.ShortTime(conversation.Last.SentAt);
        Preview = conversation is null ? L.T("Chưa có tin nhắn")
            : L.F("{0}: {1}", author, TransferText.Preview(ChatBubbleViewModel.TextOf(conversation.Last.Kind, conversation.Last.Text, conversation.Last.DurationMs)));
        return this;
    }

    /// <param name="others">The members besides us; null when we are no longer in the group.</param>
    internal void Refresh(Group? group, IReadOnlyList<GroupMember>? others, Dictionary<DeviceId, PeerInfo> live)
    {
        if (group is not null)
        {
            Name = group.Name;
        }

        MembersText = others is null ? L.T("Bạn không còn trong nhóm")
            : L.F("{0} thành viên · {1} online", others.Count + 1, others.Count(m => live.GetValueOrDefault(m.DeviceId) is { State: PeerState.Online }) + 1);
    }

    internal void Refresh(PeerRecord? record, PeerInfo? live, string name)
    {
        Name = name;
        IsPhone = live is null ? record?.IsPhone == true : live.Os.Equals("android", StringComparison.OrdinalIgnoreCase);
        IsOnline = live is { State: PeerState.Online };
        IsBusy = live?.IsBusy == true;
    }
}
