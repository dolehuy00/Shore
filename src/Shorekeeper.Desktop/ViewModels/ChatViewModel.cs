using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// "Tin nhắn" (docs/15-chat.md §6), on the desktop and the phone: conversations, newest first, then contacts not
/// written to yet; the open one's messages and the box to write in. What the peer sent is read while the
/// conversation is shown (<see cref="IsShown"/>, set by the window).
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ChatService chat;
    private readonly TrustStore trust;
    private readonly PeerDirectory directory;
    private readonly Dictionary<DeviceId, ChatPersonViewModel> people = [];

    public ChatViewModel(ChatService chat, TrustStore trust, PeerDirectory directory)
    {
        this.chat = chat;
        this.trust = trust;
        this.directory = directory;
        chat.Changed += (_, peer) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(peer));
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(null));
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPresence);
        _ = RefreshAsync(null);
    }

    public ObservableCollection<ChatPersonViewModel> Conversations { get; } = [];

    /// <summary>The open conversation, oldest first.</summary>
    public ObservableCollection<ChatBubbleViewModel> Messages { get; } = [];

    public bool HasConversations => Conversations.Count > 0;

    /// <summary>Messages not read yet, from everyone: the badge on "Tin nhắn".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadTotal { get; set; }

    public bool HasUnread => UnreadTotal > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial ChatPersonViewModel? Current { get; set; }

    public bool IsOpen => Current is not null;

    public bool HasNoMessages => Current is not null && Messages.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    /// <summary>Writing is possible: a contact whose version takes messages (or might).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool CanWrite { get; set; }

    /// <summary>Above the box: why one cannot write, or that the peer is offline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    /// <summary>The conversation is on screen: the page is shown and the window is in front.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>The peer whose messages are being read right now, so no notification is needed for them.</summary>
    public DeviceId? ReadingPeer => IsShown ? Current?.Peer : null;

    /// <summary>New messages were added at the end: the view scrolls down to them.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>Opens the conversation with <paramref name="peer"/>, from the list, "Nhắn tin" or a notification.</summary>
    public void Open(DeviceId peer)
    {
        ChatPersonViewModel person = Person(peer);
        if (!Conversations.Contains(person))
        {
            Conversations.Add(person);
            OnPropertyChanged(nameof(HasConversations));
        }

        if (Current != person)
        {
            Messages.Clear();
            Draft = "";
            Current = person;
        }

        _ = RefreshAsync(peer);
    }

    [RelayCommand]
    private void Close() => Current = null;

    private bool CanSend() => CanWrite && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Current is not { } person)
        {
            return;
        }

        if (Draft.Trim().Length > ChatService.MaxTextLength)
        {
            Notice = L.F("Tin nhắn dài tối đa {0:N0} ký tự.", ChatService.MaxTextLength);
            return;
        }

        string text = Draft;
        Draft = "";
        await chat.SendTextAsync(person.Peer, text, CancellationToken.None);
    }

    internal Task RetryAsync(ChatBubbleViewModel bubble) =>
        Current is { } person ? chat.RetryAsync(person.Peer, bubble.Id, CancellationToken.None) : Task.CompletedTask;

    partial void OnCurrentChanged(ChatPersonViewModel? oldValue, ChatPersonViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsCurrent = false;
        }

        if (newValue is not null)
        {
            newValue.IsCurrent = true;
        }
        else
        {
            Messages.Clear();
        }

        OnPropertyChanged(nameof(HasNoMessages));
        RefreshPresence();
    }

    partial void OnIsShownChanged(bool value)
    {
        if (value && Current is { } person)
        {
            _ = RefreshAsync(person.Peer);
        }
    }

    /// <param name="peer">The conversation that changed; null when contacts changed.</param>
    private async Task RefreshAsync(DeviceId? peer)
    {
        IReadOnlyList<ChatConversation> conversations = await chat.GetConversationsAsync(CancellationToken.None);
        List<ChatPersonViewModel> ordered = [];
        foreach (ChatConversation conversation in conversations)
        {
            ordered.Add(Person(conversation.Peer).Update(conversation));
        }

        // Contacts not written to yet come after, by name, so a conversation can start from here too.
        ordered.AddRange(trust.GetAll(TrustLevel.Trusted)
            .Where(c => !ordered.Any(p => p.Peer == c.DeviceId))
            .OrderBy(c => c.ShownName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Person(c.DeviceId).Update(null)));
        if (Current is { } current && !ordered.Contains(current))
        {
            ordered.Add(current);
        }

        Sync(Conversations, ordered);
        UnreadTotal = conversations.Sum(c => c.Unread);
        OnPropertyChanged(nameof(HasConversations));
        RefreshPresence();

        if (Current is { } open && (peer is null || peer == open.Peer))
        {
            await RefreshMessagesAsync(open);
        }
    }

    private async Task RefreshMessagesAsync(ChatPersonViewModel person)
    {
        IReadOnlyList<ChatMessage> messages = await chat.GetMessagesAsync(person.Peer, CancellationToken.None);
        if (Current != person)
        {
            return;
        }

        // Unless the order changed (a late message from a clock behind), only states change and messages are added.
        bool prefix = Messages.Count <= messages.Count && Messages.Select(b => b.Id).SequenceEqual(messages.Take(Messages.Count).Select(m => m.Id));
        if (!prefix)
        {
            Messages.Clear();
        }

        int added = messages.Count - Messages.Count;
        string? lastMine = messages.LastOrDefault(m => m.Outgoing)?.Id;
        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            DateTime day = message.SentAt.ToLocalTime().Date;
            string? dayText = i == 0 || messages[i - 1].SentAt.ToLocalTime().Date != day ? DayText(day) : null;
            if (i < Messages.Count)
            {
                Messages[i].Update(message, dayText, message.Id == lastMine);
            }
            else
            {
                Messages.Add(new ChatBubbleViewModel(message.Id, this).Update(message, dayText, message.Id == lastMine));
            }
        }

        OnPropertyChanged(nameof(HasNoMessages));
        if (added > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }

        if (IsShown && messages.Any(m => m.State == ChatMessageState.Unread))
        {
            await chat.MarkReadAsync(person.Peer, CancellationToken.None);
        }
    }

    /// <summary>Names, online dots and whether the open conversation can be written to.</summary>
    private void RefreshPresence()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        foreach (ChatPersonViewModel person in Conversations)
        {
            person.Refresh(trust.Get(person.Peer), live.GetValueOrDefault(person.Peer));
        }

        if (Current is not { } current)
        {
            CanWrite = false;
            Notice = null;
            return;
        }

        PeerInfo? peer = live.GetValueOrDefault(current.Peer);
        if (trust.GetLevel(current.Peer) != TrustLevel.Trusted)
        {
            CanWrite = false;
            Notice = L.T("Chưa kết nối với máy này nên không nhắn tin được.");
        }
        else if (peer is { Source: PeerSource.Direct, DiscoveryPort: > 0, CanChat: false })
        {
            // Heard directly, so its presence is complete: a version without messages.
            CanWrite = false;
            Notice = L.T("Máy này chưa cập nhật Shorekeeper nên chưa nhận được tin nhắn.");
        }
        else
        {
            CanWrite = true;
            Notice = peer is null ? L.T("Máy này đang offline: tin nhắn sẽ được gửi khi máy online.") : null;
        }
    }

    private ChatPersonViewModel Person(DeviceId peer)
    {
        if (!people.TryGetValue(peer, out ChatPersonViewModel? person))
        {
            person = new ChatPersonViewModel(peer, this);
            person.Refresh(trust.Get(peer), directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer));
            people[peer] = person;
        }

        return person;
    }

    /// <summary>Moves items into place, so rows keep their state instead of being built again.</summary>
    private static void Sync(ObservableCollection<ChatPersonViewModel> target, List<ChatPersonViewModel> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = target.IndexOf(wanted[i]);
            if (at < 0)
            {
                target.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                target.Move(at, i);
            }
        }

        while (target.Count > wanted.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>"Hôm nay", "Hôm qua", "20/10/2026".</summary>
    private static string DayText(DateTime day) =>
        day == DateTime.Today ? L.T("Hôm nay")
        : day == DateTime.Today.AddDays(-1) ? L.T("Hôm qua")
        : day.ToString("dd/MM/yyyy", L.Culture);

    /// <summary>In the list: the time today, "Hôm qua", else the date.</summary>
    internal static string ShortTime(DateTimeOffset at)
    {
        DateTime local = at.ToLocalTime().DateTime;
        return local.Date == DateTime.Today ? local.ToString("HH:mm", L.Culture)
            : local.Date == DateTime.Today.AddDays(-1) ? L.T("Hôm qua")
            : local.ToString("dd/MM", L.Culture);
    }
}

/// <summary>A row of "Tin nhắn": a contact, with the last message when there is one.</summary>
public sealed partial class ChatPersonViewModel(DeviceId peer, ChatViewModel owner) : ObservableObject
{
    public DeviceId Peer { get; } = peer;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPhone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsBusy { get; set; }

    public string StatusText => !IsOnline ? "Offline" : IsBusy ? L.T("Bận") : "Online";

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
    private void Open() => owner.Open(Peer);

    internal ChatPersonViewModel Update(ChatConversation? conversation)
    {
        Unread = conversation?.Unread ?? 0;
        TimeText = conversation is null ? "" : ChatViewModel.ShortTime(conversation.Last.SentAt);
        Preview = conversation is null ? L.T("Chưa có tin nhắn")
            : conversation.Last.Outgoing ? L.F("Bạn: {0}", TransferText.Preview(ChatBubbleViewModel.TextOf(conversation.Last)))
            : TransferText.Preview(ChatBubbleViewModel.TextOf(conversation.Last));
        return this;
    }

    internal void Refresh(PeerRecord? record, PeerInfo? live)
    {
        Name = record?.ShownName ?? live?.DisplayName ?? Peer.ShortForm;
        IsPhone = live is null ? record?.IsPhone == true : live.Os.Equals("android", StringComparison.OrdinalIgnoreCase);
        IsOnline = live is { State: PeerState.Online };
        IsBusy = live?.IsBusy == true;
    }
}

/// <summary>A message in the open conversation.</summary>
public sealed partial class ChatBubbleViewModel(string id, ChatViewModel owner) : ObservableObject
{
    public string Id { get; } = id;

    public bool IsMine { get; private set; }

    public string Text { get; private set; } = "";

    /// <summary>Set on the first message of a day: "Hôm nay", "20/10/2026".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDay))]
    public partial string? DayText { get; set; }

    public bool HasDay => DayText is not null;

    /// <summary>"14:05", with the state on ours: "14:05 · Đã xem".</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    /// <summary>A kind from a newer version: shown as a note, not as the sender's words.</summary>
    public bool IsUnknown { get; private set; }

    [RelayCommand]
    private Task RetryAsync() => owner.RetryAsync(this);

    /// <param name="lastMine">Our newest message: it says how far the peer got even when delivered or read.</param>
    internal ChatBubbleViewModel Update(ChatMessage message, string? dayText, bool lastMine)
    {
        IsMine = message.Outgoing;
        IsUnknown = message.Kind != ChatKinds.Text;
        Text = TextOf(message);
        DayText = dayText;
        IsFailed = message.State == ChatMessageState.Failed;
        string time = message.SentAt.ToLocalTime().ToString("HH:mm", L.Culture);
        string? state = !message.Outgoing ? null : message.State switch
        {
            ChatMessageState.Pending => L.T("Chờ gửi"),
            ChatMessageState.Failed => L.T("Không gửi được"),
            ChatMessageState.Delivered when lastMine => L.T("Đã nhận"),
            ChatMessageState.Read when lastMine => L.T("Đã xem"),
            _ => null,
        };
        StatusText = state is null ? time : $"{time} · {state}";
        return this;
    }

    internal static string TextOf(ChatMessage message) =>
        message.Kind == ChatKinds.Text ? message.Text ?? "" : L.T("Tin nhắn này cần bản Shorekeeper mới hơn.");
}
