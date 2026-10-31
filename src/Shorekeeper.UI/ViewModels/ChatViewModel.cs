using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Text;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// "Tin nhắn" (docs/15-chat.md §6), on the desktop and the phone: conversations, newest first, then contacts not
/// written to yet; the open one's messages and the box to write in. What the peer sent is read while the
/// conversation is shown (<see cref="IsShown"/>, set by the window). Voice messages are recorded with the microphone
/// and played one at a time (docs/15-chat.md §3). Files sent between the two show among the messages as cards (§6.3).
/// Groups have conversations too (docs/16-group-chat.md §6), listed among the people's once <see cref="ShowsGroups"/>.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ChatService chat;
    private readonly GroupChatService groupChat;
    private readonly GroupStore groups;
    private readonly TrustStore trust;
    private readonly PeerDirectory directory;
    private readonly OfferService offers;
    private readonly InboxService inbox;
    private readonly SettingsService settings;
    private readonly PeerNaming naming;
    private readonly IExternalFileSource? external;
    private readonly IMicrophone? microphone;
    private readonly IVoicePlayer? player;
    private readonly Dictionary<DeviceId, ChatPersonViewModel> people = [];
    private readonly Dictionary<string, ChatPersonViewModel> groupRows = [];
    private IReadOnlySet<string> muted = new HashSet<string>();
    private VoiceRecorder? recorder;
    private IDisposable? recording;
    private IDisposable? recordingTimer;
    private (ChatBubbleViewModel Bubble, VoiceNotePlayer Note, IDisposable Output, DispatcherTimer Timer)? playing;
    private bool filesQueued;

    public ChatViewModel(
        ChatService chat, GroupChatService groupChat, GroupStore groups, TrustStore trust, PeerDirectory directory, OfferService offers,
        InboxService inbox, SettingsService settings, PeerNaming naming, IExternalFileSource? external = null, IMicrophone? microphone = null,
        IVoicePlayer? player = null)
    {
        this.naming = naming;
        this.chat = chat;
        this.groupChat = groupChat;
        this.groups = groups;
        this.trust = trust;
        this.directory = directory;
        this.offers = offers;
        this.inbox = inbox;
        this.settings = settings;
        this.external = external;
        this.microphone = microphone;
        this.player = player;
        chat.Changed += (_, peer) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(peer));
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(null));
        groupChat.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(null));
        groups.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(null));
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPresence);
        // Progress comes several times a second while files move: one refresh at a time is enough.
        offers.Changed += (_, _) => QueueFilesRefresh();
        inbox.Changed += (_, _) => QueueFilesRefresh();
        inbox.OfferArrived += (_, _) => QueueFilesRefresh();
        // "Thu hồi" leaves the menus once its time is up (ChatService.RecallWindow), with nothing else changing.
        recallClock = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal, (_, _) =>
        {
            foreach (ChatEntryViewModel entry in Messages)
            {
                entry.RefreshRecall();
            }
        });
        _ = RefreshAsync(null);
    }

    /// <summary>Kept so the timer is not collected.</summary>
    private readonly DispatcherTimer recallClock;

    /// <summary>Puts text on the clipboard (set by the window).</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Opens a web address in the browser (set by the window).</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Asks before something that cannot be undone: title, question, action; true to go ahead (set by the window).</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Asks for a line of text: title, label, the text to start with; null when cancelled.</summary>
    public Func<string, string, string, Task<string?>>? AskText { get; set; }

    /// <summary>"Xem" on a file card: the window shows Hộp nhận (true, received) or Đã gửi.</summary>
    public Action<bool>? ShowFiles { get; set; }

    /// <summary>Opens a received or sent picture in the system's viewer (set by the window); without it, "Xem" happens.</summary>
    public Action<string>? OpenFile { get; set; }

    /// <summary>Cài đặt: pictures from contacts download by themselves and show in the conversation (on by default).</summary>
    public bool AutoDownloadPictures
    {
        get => settings.Current.ChatAutoDownloadPictures;
        set
        {
            settings.Update(s => s with { ChatAutoDownloadPictures = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Group conversations are listed too (docs/16-group-chat.md §6), on the platforms whose views show them.</summary>
    [ObservableProperty]
    public partial bool ShowsGroups { get; set; }

    partial void OnShowsGroupsChanged(bool value) => _ = RefreshAsync(null);

    /// <summary>The system's file picker for 📎 (set by the window); empty when nothing was picked.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>
    /// Sends files or folders as one offer to these people, through the group when one is given (set by the window); a
    /// problem to show, or null.
    /// </summary>
    public Func<IReadOnlyList<DeviceId>, string?, IReadOnlyList<string>, Task<string?>>? SendFiles { get; set; }

    /// <summary>
    /// Files can go to the open conversation: a contact, or the group's other members (docs/16-group-chat.md §6.3).
    /// Unlike messages this does not need a version with chat, as files always went.
    /// </summary>
    public bool CanAttach => SendFiles is not null && Current switch
    {
        { GroupId: { } groupId } => groupChat.OthersIn(groupId) is { Count: > 0 },
        { } person => trust.GetLevel(person.Peer) == TrustLevel.Trusted,
        _ => false,
    };

    /// <summary>Files are being dragged over the conversation (desktop).</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>📎: pick files and send them to this person; they show up as a card.</summary>
    [RelayCommand]
    private async Task AttachAsync()
    {
        if (CanAttach && PickFiles is { } pick && await pick() is { Count: > 0 } picked)
        {
            await AttachAsync(picked);
        }
    }

    /// <summary>Files dropped or pasted into the conversation, or picked with 📎: to the person, or to every other member.</summary>
    public async Task AttachAsync(IReadOnlyList<string> paths)
    {
        if (!CanAttach || Current is not { } row || paths.Count == 0)
        {
            return;
        }

        IReadOnlyList<DeviceId> to = row.GroupId is { } groupId ? [.. groupChat.OthersIn(groupId)!.Select(m => m.DeviceId)] : [row.Peer];
        if (await SendFiles!(to, row.GroupId, paths) is { } problem)
        {
            Notice = problem;
        }
    }

    public ObservableCollection<ChatPersonViewModel> Conversations { get; } = [];

    /// <summary>The open conversation, oldest first: messages, and the files sent between the two.</summary>
    public ObservableCollection<ChatEntryViewModel> Messages { get; } = [];

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
    [NotifyPropertyChangedFor(nameof(ShowsMicrophone), nameof(ShowsSend))]
    public partial string Draft { get; set; } = "";

    /// <summary>Phone: an empty box has 🎤 (hold to record) where ➤ would be.</summary>
    public bool ShowsMicrophone => HasMicrophone && string.IsNullOrWhiteSpace(Draft);

    public bool ShowsSend => !ShowsMicrophone;

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
    public DeviceId? ReadingPeer => IsShown && Current is { IsGroup: false } person ? person.Peer : null;

    /// <summary>The group whose messages are being read right now.</summary>
    public string? ReadingGroup => IsShown ? Current?.GroupId : null;

    /// <summary>New messages were added at the end: the view scrolls down to them.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>A search result was opened: the view scrolls to this entry and points at it.</summary>
    public event EventHandler<ChatEntryViewModel>? RevealRequested;

    /// <summary>Shown once the conversation <see cref="OpenAt"/> asked for has loaded.</summary>
    private string? revealId;

    /// <summary>At most this many messages are found, newest first.</summary>
    public const int MaxHits = 50;

    /// <summary>
    /// Messages with every word of <paramref name="query"/>, as Ctrl+K reads it (docs/18-chat-extras.md §3): text messages
    /// of every conversation, and text sent as a card. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<ChatSearchHit>> FindAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        bool withGroups = ShowsGroups && settings.Current.GroupsEnabled;
        IReadOnlyList<ChatMessage> direct = await chat.FindAsync(query, MaxHits, cancellationToken);
        IReadOnlyList<GroupChatMessage> inGroups = withGroups ? await groupChat.FindAsync(query, MaxHits, cancellationToken) : [];
        string me = L.T("Bạn");
        List<ChatSearchHit> hits =
        [
            .. direct.Select(m => new ChatSearchHit(
                NameOf(m.Peer), Said(m.Outgoing ? me : NameOf(m.Peer), m.Text!, query), m.SentAt, m.Peer, null, m.Id, this)),
            .. inGroups.Select(m => new ChatSearchHit(
                GroupNameOf(m.GroupId), Said(m.Outgoing ? me : NameOf(m.GroupId, m.Author, m.AuthorName), m.Text!, query), m.SentAt, null, m.GroupId, m.Id, this)),
            // Text sent as a card ("Gửi văn bản"): one hit in each conversation it shows in.
            .. offers.GetOffers()
                .Where(o => o.Paths.Count == 0 && !o.Recalled && TextSearch.Matches(query, o.Note) && (o.GroupId is null || withGroups))
                .SelectMany(o => o.GroupId is { } groupId
                    ? [new ChatSearchHit(GroupNameOf(groupId), Said(me, o.Note!, query), o.CreatedAt, null, groupId, "sent:" + o.OfferId, this)]
                    : o.Recipients.Select(r => new ChatSearchHit(
                        NameOf(r.DeviceId), Said(me, o.Note!, query), o.CreatedAt, r.DeviceId, null, "sent:" + o.OfferId, this))),
            .. inbox.GetOffers()
                .Where(o => o.Files.Count == 0 && !o.Recalled && TextSearch.Matches(query, o.Note) && (o.GroupId is null || withGroups))
                .Select(o => o.GroupId is { } groupId
                    ? new ChatSearchHit(GroupNameOf(groupId), Said(NameOf(o.SenderId), o.Note!, query), o.SentAt, null, groupId, "received:" + o.OfferId, this)
                    : new ChatSearchHit(NameOf(o.SenderId), Said(NameOf(o.SenderId), o.Note!, query), o.SentAt, o.SenderId, null, "received:" + o.OfferId, this)),
        ];
        return [.. hits.OrderByDescending(h => h.At).Take(MaxHits)];
    }

    /// <summary>"Mai: …nhớ mang báo cáo…".</summary>
    private static string Said(string author, string text, string query) => $"{author}: {TransferText.Snippet(text, query)}";

    /// <summary>Opens the conversation a search result is in, at that message.</summary>
    public void OpenAt(ChatSearchHit hit)
    {
        SearchQuery = "";
        if (hit.GroupId is { } groupId)
        {
            OpenGroup(groupId);
        }
        else if (hit.Peer is { } peer)
        {
            Open(peer);
        }

        // Already there: point at it now; otherwise once the conversation has loaded.
        if (Messages.FirstOrDefault(m => m.Id == hit.EntryId) is { } entry)
        {
            revealId = null;
            RevealRequested?.Invoke(this, entry);
        }
        else
        {
            revealId = hit.EntryId;
        }
    }

    /// <summary>After a conversation loaded: the message a search result asked for, or new ones at the end.</summary>
    private void Scroll(int added)
    {
        if (revealId is { } id && Messages.FirstOrDefault(m => m.Id == id) is { } entry)
        {
            revealId = null;
            RevealRequested?.Invoke(this, entry);
        }
        else if (added > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The phone's search box above the conversations (the desktop searches with Ctrl+K).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string SearchQuery { get; set; } = "";

    /// <summary>The box has words: its results show instead of the conversations.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);

    public ObservableCollection<ChatSearchHit> SearchHits { get; } = [];

    [ObservableProperty]
    public partial bool HasNoSearchHits { get; set; }

    /// <summary>Counts the words typed: only the newest search shows its results.</summary>
    private int searchVersion;

    partial void OnSearchQueryChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string query)
    {
        int version = ++searchVersion;
        // A short pause, so each letter typed does not read the messages again.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        if (version != searchVersion)
        {
            return;
        }

        IReadOnlyList<ChatSearchHit> hits = await FindAsync(query, CancellationToken.None);
        if (version != searchVersion)
        {
            return;
        }

        SearchHits.Clear();
        foreach (ChatSearchHit hit in hits)
        {
            SearchHits.Add(hit);
        }

        HasNoSearchHits = IsSearching && hits.Count == 0;
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    /// <summary>Opens the conversation with <paramref name="peer"/>, from the list, "Nhắn tin" or a notification.</summary>
    public void Open(DeviceId peer) => Open(Person(peer));

    /// <summary>Opens a group's conversation, from the list, "Nhắn tin" on the group or a notification.</summary>
    public void OpenGroup(string groupId) => Open(GroupRow(groupId));

    internal void Open(ChatPersonViewModel row)
    {
        if (!Conversations.Contains(row))
        {
            Conversations.Add(row);
            OnPropertyChanged(nameof(HasConversations));
        }

        if (Current != row)
        {
            Messages.Clear();
            Draft = "";
            Current = row;
        }

        _ = RefreshAsync(row.IsGroup ? null : row.Peer);
    }

    /// <summary>Voice messages can be recorded here.</summary>
    public bool HasMicrophone => microphone is not null;

    /// <summary>Asked before recording, e.g. for Android's microphone permission; false: do not record (this time).</summary>
    public Func<Task<bool>>? AllowRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposing))]
    public partial bool IsRecording { get; set; }

    /// <summary>The box, not a recording, is at the bottom.</summary>
    public bool IsComposing => !IsRecording;

    /// <summary>"0:07" while recording.</summary>
    [ObservableProperty]
    public partial string RecordingText { get; set; } = "";

    /// <summary>How loud the microphone is, 0 to 100.</summary>
    [ObservableProperty]
    public partial double RecordingLevel { get; set; }

    /// <summary>The finger slid far enough to cancel (phone): letting go throws the recording away.</summary>
    [ObservableProperty]
    public partial bool WillCancel { get; set; }

    [RelayCommand]
    private void Close() => Current = null;

    [RelayCommand]
    private async Task RecordAsync() => await StartRecordingAsync();

    [RelayCommand]
    private Task SendRecordingAsync() => FinishRecordingAsync(send: true);

    [RelayCommand]
    private Task CancelRecordingAsync() => FinishRecordingAsync(send: false);

    /// <returns>Whether the microphone is recording now.</returns>
    public async Task<bool> StartRecordingAsync()
    {
        if (microphone is null || !CanWrite || IsRecording)
        {
            return false;
        }

        StopPlaying();
        if (AllowRecording is { } allow && !await allow())
        {
            return false;
        }

        var next = new VoiceRecorder();
        try
        {
            recording = microphone.Start(next.Add);
        }
        catch (InvalidOperationException ex)
        {
            Notice = L.F("Không mở được micrô: {0}", ex.Message);
            return false;
        }

        recorder = next;
        WillCancel = false;
        RecordingText = Duration(0);
        RecordingLevel = 0;
        IsRecording = true;
        recordingTimer = DispatcherTimer.Run(() =>
        {
            RecordingText = Duration(next.DurationMs);
            RecordingLevel = 100 * Math.Sqrt(next.Level);
            if (next.IsFull)
            {
                _ = FinishRecordingAsync(send: true);
            }

            return IsRecording;
        }, TimeSpan.FromMilliseconds(100));
        return true;
    }

    /// <summary>Stops recording; with <paramref name="send"/>, sends what was recorded, unless it is too short to mean anything.</summary>
    public async Task FinishRecordingAsync(bool send)
    {
        if (!IsRecording || recorder is not { } done)
        {
            return;
        }

        recording?.Dispose();
        recording = null;
        recordingTimer?.Dispose();
        recorder = null;
        IsRecording = false;
        WillCancel = false;
        if (!send || Current is not { } person)
        {
            return;
        }

        VoiceNote note = done.Finish();
        if (note.DurationMs < 500)
        {
            Notice = L.T("Tin nhắn thoại ngắn quá, chưa gửi.");
            return;
        }

        if (person.GroupId is { } groupId)
        {
            await groupChat.SendVoiceAsync(groupId, note, CancellationToken.None);
        }
        else
        {
            await chat.SendVoiceAsync(person.Peer, note, CancellationToken.None);
        }
    }

    /// <summary>▶ on a voice message: plays it, or stops it when it is the one playing.</summary>
    internal void TogglePlay(ChatBubbleViewModel bubble)
    {
        bool same = playing?.Bubble == bubble;
        StopPlaying();
        if (same || player is null || IsRecording)
        {
            return;
        }

        VoiceNotePlayer? note = bubble.VoicePath is { } path && File.Exists(path) ? VoiceNotePlayer.Open(File.ReadAllBytes(path)) : null;
        if (note is null)
        {
            Notice = L.T("Không mở được tin nhắn thoại này.");
            return;
        }

        IDisposable output = player.Start(note.Read);
        bubble.IsPlaying = true;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) =>
        {
            bubble.Progress = Math.Min(1, (double)note.PositionMs / Math.Max(1, bubble.DurationMs));
            if (note.HasEnded)
            {
                StopPlaying();
            }
        });
        playing = (bubble, note, output, timer);
        timer.Start();
    }

    private void StopPlaying()
    {
        if (playing is not { } now)
        {
            return;
        }

        playing = null;
        now.Timer.Stop();
        now.Output.Dispose();
        now.Bubble.IsPlaying = false;
        now.Bubble.Progress = 0;
    }

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
        if (person.GroupId is { } groupId)
        {
            await groupChat.SendTextAsync(groupId, text, CancellationToken.None);
        }
        else
        {
            await chat.SendTextAsync(person.Peer, text, CancellationToken.None);
        }
    }

    /// <summary>"Gửi lại": to the peer, or to every member it failed for.</summary>
    internal Task RetryAsync(ChatBubbleViewModel bubble) =>
        Current is { GroupId: { } groupId } ? groupChat.RetryAsync(groupId, bubble.Id, CancellationToken.None)
        : Current is { } person ? chat.RetryAsync(person.Peer, bubble.Id, CancellationToken.None)
        : Task.CompletedTask;

    partial void OnCurrentChanged(ChatPersonViewModel? oldValue, ChatPersonViewModel? newValue)
    {
        StopPlaying();
        IsSelecting = false;
        CloseDetails();
        _ = FinishRecordingAsync(send: false);
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
        OnPropertyChanged(nameof(CanAttach));
        RefreshPresence();
    }

    partial void OnIsShownChanged(bool value)
    {
        if (value && Current is { } person)
        {
            _ = RefreshAsync(person.IsGroup ? null : person.Peer);
        }
    }

    /// <param name="peer">The conversation that changed; null when contacts or offers changed.</param>
    private async Task RefreshAsync(DeviceId? peer)
    {
        IReadOnlyList<ChatConversation> conversations = await chat.GetConversationsAsync(CancellationToken.None);
        // A file card is said in the conversation too: the newest of a message or an offer leads, by its time.
        Dictionary<DeviceId, ChatLastFile> files = LastFiles(await chat.GetClearedAsync(CancellationToken.None));
        bool withGroups = ShowsGroups && settings.Current.GroupsEnabled;
        IReadOnlyList<GroupChatConversation> groupConversations = withGroups ? await groupChat.GetConversationsAsync(CancellationToken.None) : [];
        Dictionary<string, ChatLastFile> groupFiles = withGroups ? LastGroupFiles(await groupChat.GetClearedAsync(CancellationToken.None)) : [];
        muted = withGroups ? await groupChat.GetMutedAsync(CancellationToken.None) : new HashSet<string>();
        List<ChatPersonViewModel> ordered =
        [
            .. conversations.Select(c => c.Peer).Union(files.Keys)
                .Select(p => (Peer: p, Conversation: conversations.FirstOrDefault(c => c.Peer == p), File: files.GetValueOrDefault(p)))
                .Select(x => (
                    At: x.File is { } file && (x.Conversation is null || file.At > x.Conversation.Last.SentAt) ? file.At : x.Conversation!.Last.SentAt,
                    Row: Person(x.Peer).Update(x.Conversation, x.File)))
                .Concat(groupConversations.Select(c => c.GroupId).Union(groupFiles.Keys.Where(g => groups.Get(g) is not null))
                    .Select(g => (Group: g, Conversation: groupConversations.FirstOrDefault(c => c.GroupId == g), File: groupFiles.GetValueOrDefault(g)))
                    .Select(x => (
                        At: x.File is { } file && (x.Conversation is null || file.At > x.Conversation.Last.SentAt) ? file.At : x.Conversation!.Last.SentAt,
                        Row: GroupRow(x.Group).Update(
                            x.Conversation, x.Conversation is { } c ? AuthorOf(x.Group, c.Last) : null, muted.Contains(x.Group),
                            x.File, x.File is { Outgoing: false, Author: { } sender } ? NameOf(x.Group, sender, sender.ShortForm) : L.T("Bạn")))))
                .OrderByDescending(x => x.At)
                .Select(x => x.Row),
        ];

        // Contacts not written to yet come after, by name, so a conversation can start from here too; then groups.
        ordered.AddRange(trust.GetAll(TrustLevel.Trusted)
            .Where(c => !ordered.Any(p => !p.IsGroup && p.Peer == c.DeviceId))
            .OrderBy(c => c.ShownName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Person(c.DeviceId).Update(null, null)));
        if (withGroups)
        {
            ordered.AddRange(groups.GetAll()
                .Where(g => g.Role != GroupRole.Pending && !ordered.Any(r => r.GroupId == g.Id))
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => GroupRow(g.Id).Update(null, null, muted.Contains(g.Id))));
        }

        if (Current is { } current && !ordered.Contains(current))
        {
            // Open with nothing left in it (just deleted): nothing to say under its name either.
            ordered.Add(current.GroupId is { } open ? current.Update(null, null, muted.Contains(open)) : current.Update(null, null));
        }

        Sync(Conversations, ordered);
        OnPropertyChanged(nameof(CanAttach));
        UnreadTotal = conversations.Sum(c => c.Unread) + groupConversations.Sum(c => c.Unread);
        OnPropertyChanged(nameof(HasConversations));
        RefreshPresence();

        if (Current is { } shown && (peer is null || (!shown.IsGroup && peer == shown.Peer)))
        {
            await RefreshMessagesAsync(shown);
        }
    }

    private async Task RefreshMessagesAsync(ChatPersonViewModel person)
    {
        if (person.GroupId is { } groupId)
        {
            await RefreshGroupMessagesAsync(person, groupId);
            return;
        }

        IReadOnlyList<ChatMessage> messages = await chat.GetMessagesAsync(person.Peer, CancellationToken.None);
        IReadOnlyDictionary<DeviceId, DateTimeOffset> cleared = await chat.GetClearedAsync(CancellationToken.None);
        if (Current != person)
        {
            return;
        }

        // Messages and file cards by time; a card's id is the offer's, which never looks like a message id.
        List<(DateTimeOffset At, string Id, object Source)> entries =
        [
            .. messages.Select(m => (m.SentAt, m.Id, (object)m)),
            .. offers.GetOffers()
                .Where(o => o.GroupId is null && o.Recipients.Any(r => r.DeviceId == person.Peer) && After(cleared, person.Peer, o.CreatedAt))
                .Select(o => (o.CreatedAt, "sent:" + o.OfferId, (object)o)),
            .. inbox.GetOffers()
                .Where(o => o.SenderId == person.Peer && o.GroupId is null && After(cleared, person.Peer, o.ReceivedAt))
                .Select(o => (o.SentAt, "received:" + o.OfferId, (object)o)),
        ];
        entries.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : string.CompareOrdinal(x.Id, y.Id));

        // Unless the order changed (a late message from a clock behind), only states change and entries are added.
        bool prefix = Messages.Count <= entries.Count && Messages.Select(b => b.Id).SequenceEqual(entries.Take(Messages.Count).Select(e => e.Id));
        if (!prefix)
        {
            Messages.Clear();
        }

        int added = entries.Count - Messages.Count;
        string? lastMine = messages.LastOrDefault(m => m.Outgoing)?.Id;
        for (int i = 0; i < entries.Count; i++)
        {
            DateTime day = entries[i].At.ToLocalTime().Date;
            string? dayText = i == 0 || entries[i - 1].At.ToLocalTime().Date != day ? DayText(day) : null;
            ChatEntryViewModel entry = i < Messages.Count ? Messages[i] : entries[i].Source is ChatMessage
                ? new ChatBubbleViewModel(entries[i].Id, this)
                : new ChatFileViewModel(entries[i].Id, this);
            entry.DayText = dayText;
            switch (entry, entries[i].Source)
            {
                case (ChatBubbleViewModel bubble, ChatMessage message):
                    bubble.Update(message, message.Id == lastMine);
                    break;
                case (ChatFileViewModel card, _):
                    ShowCard(card, entries[i].Source, person.Peer, person.IsOnline);
                    break;
            }

            if (i >= Messages.Count)
            {
                Messages.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasNoMessages));
        if (IsSelecting)
        {
            SelectionChanged();
        }

        Scroll(added);

        if (IsShown && messages.Any(m => m.State == ChatMessageState.Unread))
        {
            await chat.MarkReadAsync(person.Peer, CancellationToken.None);
        }
    }

    /// <summary>
    /// A group conversation (docs/16-group-chat.md §6.1): each member's messages under their name, how far our newest one
    /// got among the members ("Đã nhận 4/7", "Đã xem 3/7"), and the files sent through the group as cards (§6.3).
    /// </summary>
    private async Task RefreshGroupMessagesAsync(ChatPersonViewModel row, string groupId)
    {
        IReadOnlyList<GroupChatMessage> messages = await groupChat.GetMessagesAsync(groupId, CancellationToken.None);
        GroupChatMessage? lastMine = messages.LastOrDefault(m => m.Outgoing);
        IReadOnlyList<GroupDelivery> reached = lastMine is null ? [] : await groupChat.GetDeliveriesAsync(groupId, lastMine.Id, CancellationToken.None);
        DateTimeOffset? clearedAt = (await groupChat.GetClearedAsync(CancellationToken.None)).TryGetValue(groupId, out DateTimeOffset at) ? at : null;
        if (Current != row)
        {
            return;
        }

        // Messages and file cards by time, each with who wrote it ("" for us); offer ids never look like message ids.
        List<(DateTimeOffset At, string Id, string Author, object Source)> entries =
        [
            .. messages.Select(m => (m.SentAt, m.Id, m.Outgoing ? "" : m.Author.Value, (object)m)),
            .. offers.GetOffers()
                .Where(o => o.GroupId == groupId && (clearedAt is null || o.CreatedAt > clearedAt))
                .Select(o => (o.CreatedAt, "sent:" + o.OfferId, "", (object)o)),
            .. inbox.GetOffers()
                .Where(o => o.GroupId == groupId && (clearedAt is null || o.ReceivedAt > clearedAt))
                .Select(o => (o.SentAt, "received:" + o.OfferId, o.SenderId.Value, (object)o)),
        ];
        entries.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : string.CompareOrdinal(x.Id, y.Id));

        // Unless the order changed, only states change and entries are added.
        bool prefix = Messages.Count <= entries.Count && Messages.Select(b => b.Id).SequenceEqual(entries.Take(Messages.Count).Select(e => e.Id));
        if (!prefix)
        {
            Messages.Clear();
        }

        int added = entries.Count - Messages.Count;
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        for (int i = 0; i < entries.Count; i++)
        {
            DateTime day = entries[i].At.ToLocalTime().Date;
            bool newDay = i == 0 || entries[i - 1].At.ToLocalTime().Date != day;
            ChatEntryViewModel entry = i < Messages.Count ? Messages[i] : entries[i].Source is GroupChatMessage
                ? new ChatBubbleViewModel(entries[i].Id, this)
                : new ChatFileViewModel(entries[i].Id, this);
            entry.DayText = newDay ? DayText(day) : null;
            switch (entry, entries[i].Source)
            {
                case (ChatBubbleViewModel bubble, GroupChatMessage message):
                    // The name over the first of each run of one member's messages.
                    bool first = newDay || entries[i - 1].Author != entries[i].Author || entries[i - 1].Source is not GroupChatMessage;
                    bubble.Update(message, AuthorOf(groupId, message), first, message == lastMine ? reached : null);
                    break;
                case (ChatFileViewModel card, ReceivedOffer received):
                    ShowCard(card, received, null, live.GetValueOrDefault(received.SenderId) is { State: PeerState.Online });
                    card.ShowAuthor(NameOf(groupId, received.SenderId, received.SenderId.ShortForm), received.SenderId);
                    break;
                case (ChatFileViewModel card, SentOffer sent):
                    ShowCard(card, sent, null, false);
                    break;
            }

            if (i >= Messages.Count)
            {
                Messages.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasNoMessages));
        if (IsSelecting)
        {
            SelectionChanged();
        }

        Scroll(added);

        if (IsShown && messages.Any(m => m.State == ChatMessageState.Unread))
        {
            await groupChat.MarkReadAsync(groupId, CancellationToken.None);
        }
    }

    /// <summary>
    /// A file card from its offer: ours to <paramref name="peer"/> (to each member of the group when null), or received.
    /// Only offers of pictures and nothing else show as pictures; by their names, as an Android source
    /// (content://…/document/msf:1234) need not have one.
    /// </summary>
    private static void ShowCard(ChatFileViewModel card, object offer, DeviceId? peer, bool senderOnline)
    {
        switch (offer)
        {
            case SentOffer sent:
                card.Update(sent, peer);
                card.ShowPictures(!sent.Recalled && sent.Sources is { Count: > 0 } sources && sent.Paths.Count > 0 && sent.Paths.All(StorageReport.IsPicture) ? sources : []);
                break;
            case ReceivedOffer received:
                card.Update(received, senderOnline);
                card.ShowPictures(
                    !received.Recalled && received.Files.Count > 0 && received.Files.All(f => !f.IsDirectory && StorageReport.IsPicture(f.RelativePath))
                        ? [.. received.Files.Where(f => f.State == InboxFileState.Completed && f.FinalPath is not null && File.Exists(f.FinalPath)).Select(f => f.FinalPath!)]
                        : []);
                break;
        }
    }

    /// <summary>Who wrote a group message: "Bạn", the contact's name, the member list's, or the name it came with.</summary>
    private string AuthorOf(string groupId, GroupChatMessage message) =>
        message.Outgoing ? L.T("Bạn") : NameOf(groupId, message.Author, message.AuthorName);

    /// <summary>
    /// A member's name: as a contact, else in the group's member list, else <paramref name="fallback"/>; told apart from
    /// anyone with the same name.
    /// </summary>
    public string NameOf(string groupId, DeviceId member, string fallback) =>
        naming.Of(member, groups.Get(groupId)?.Members.FirstOrDefault(m => m.DeviceId == member)?.Name ?? fallback);

    /// <summary>Someone's name, told apart from anyone with the same name.</summary>
    internal string NameOf(DeviceId peer, string? fallback = null) => naming.Of(peer, fallback);

    /// <summary>A group's name, for a notification.</summary>
    public string GroupNameOf(string groupId) => groupRows.GetValueOrDefault(groupId)?.Name ?? groups.Get(groupId)?.Name ?? "";

    /// <summary>Notifications are off for this group ("Tắt thông báo").</summary>
    public bool IsMuted(string groupId) => muted.Contains(groupId);

    // ───────────── "Chi tiết" and "Thành viên" (docs/16-group-chat.md §6.1) ─────────────

    /// <summary>Over the conversation: who one of our messages reached, or who is in the group. Null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial string? DetailsTitle { get; set; }

    public bool HasDetails => DetailsTitle is not null;

    public ObservableCollection<ChatMemberViewModel> Details { get; } = [];

    [RelayCommand]
    private void CloseDetails()
    {
        DetailsTitle = null;
        Details.Clear();
    }

    /// <summary>⋮ → "Thành viên": everyone in the group, online first.</summary>
    [RelayCommand]
    private void ShowMembers()
    {
        if (Current?.GroupId is not { } groupId || groups.Get(groupId) is not { } group)
        {
            return;
        }

        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        IReadOnlyList<GroupMember> others = groupChat.OthersIn(groupId) ?? [];
        Details.Clear();
        foreach (GroupMember member in group.Members.OrderByDescending(m => !others.Contains(m) || live.GetValueOrDefault(m.DeviceId) is { State: PeerState.Online })
                     .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool me = !others.Contains(member);
            bool online = me || live.GetValueOrDefault(member.DeviceId) is { State: PeerState.Online };
            string state = online ? "Online" : "Offline";
            Details.Add(new ChatMemberViewModel(
                me ? L.T("Bạn") : NameOf(groupId, member.DeviceId, member.Name), member.IsHost ? $"{state} · Host" : state, online, null,
                me ? null : () => RenameAsync(member.DeviceId, member.Name)));
        }

        DetailsTitle = L.F("{0} thành viên", group.Members.Count);
    }

    /// <summary>
    /// "Đặt tên gợi nhớ…": our own name for someone, a contact or a member we are not connected with; emptied, they are
    /// shown by their own name again. <paramref name="knownAs"/> is their name when we have none (a member's name).
    /// </summary>
    internal async Task RenameAsync(DeviceId peer, string? knownAs = null)
    {
        string current = trust.GetAlias(peer) ?? trust.Get(peer)?.DisplayName ?? knownAs ?? "";
        if (AskText is { } ask && await ask(L.T("Đặt tên gợi nhớ"), L.F("Tên gợi nhớ cho {0}:", NameOf(peer, knownAs)), current) is { } alias)
        {
            await trust.SetAliasAsync(peer, alias);
        }
    }

    /// <summary>"Đặt tên gợi nhớ…" in the ⋮ of a conversation with one person.</summary>
    [RelayCommand]
    private Task RenameCurrentAsync() => Current is { IsGroup: false } person ? RenameAsync(person.Peer) : Task.CompletedTask;

    /// <summary>"Chi tiết" on one of our group messages: where it is with each member, and [Gửi lại] where it failed.</summary>
    internal async Task ShowDetailsAsync(ChatBubbleViewModel bubble)
    {
        if (Current?.GroupId is not { } groupId)
        {
            return;
        }

        IReadOnlyList<GroupDelivery> deliveries = await groupChat.GetDeliveriesAsync(groupId, bubble.Id, CancellationToken.None);
        Details.Clear();
        foreach (GroupDelivery delivery in deliveries.OrderBy(d => d.State).ThenBy(d => NameOf(groupId, d.Peer, d.Peer.ShortForm), StringComparer.CurrentCultureIgnoreCase))
        {
            Func<Task>? retry = delivery.State is GroupDeliveryState.Failed or GroupDeliveryState.Old
                ? async () =>
                {
                    await groupChat.RetryAsync(groupId, bubble.Id, delivery.Peer, CancellationToken.None);
                    await ShowDetailsAsync(bubble);
                }
                : null;
            Details.Add(new ChatMemberViewModel(
                NameOf(groupId, delivery.Peer, delivery.Peer.ShortForm), DeliveryText(delivery.State), false, retry, () => RenameAsync(delivery.Peer)));
        }

        DetailsTitle = L.T("Chi tiết tin nhắn");
    }

    private static string DeliveryText(GroupDeliveryState state) => state switch
    {
        GroupDeliveryState.Read => L.T("Đã xem"),
        GroupDeliveryState.Delivered => L.T("Đã nhận"),
        GroupDeliveryState.Pending => L.T("Chờ gửi"),
        GroupDeliveryState.Old => L.T("Bản cũ, chưa nhận được tin nhóm"),
        _ => L.T("Không gửi được"),
    };

    /// <summary>⋮ → "Tắt thông báo" / "Bật thông báo" of the open group.</summary>
    [RelayCommand]
    private Task ToggleMuteAsync() =>
        Current is { GroupId: { } groupId } row ? groupChat.SetMutedAsync(groupId, !row.IsMuted, CancellationToken.None) : Task.CompletedTask;

    private void QueueFilesRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (filesQueued)
        {
            return;
        }

        filesQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            filesQueued = false;
            // The list too: an offer may now be the last thing said with someone.
            await RefreshAsync(null);
        });
    });

    /// <summary>Sent or received after the conversation was last deleted ("Xóa hội thoại"), so its card shows.</summary>
    private static bool After(IReadOnlyDictionary<DeviceId, DateTimeOffset> cleared, DeviceId peer, DateTimeOffset at) =>
        !cleared.TryGetValue(peer, out DateTimeOffset clearedAt) || at > clearedAt;

    /// <summary>The newest offer with each contact, as its card shows in the conversation (not through a group).</summary>
    private Dictionary<DeviceId, ChatLastFile> LastFiles(IReadOnlyDictionary<DeviceId, DateTimeOffset> cleared)
    {
        Dictionary<DeviceId, ChatLastFile> last = [];
        foreach (SentOffer sent in offers.GetOffers().Where(o => o.GroupId is null))
        {
            foreach (SentRecipient recipient in sent.Recipients.Where(r => After(cleared, r.DeviceId, sent.CreatedAt)))
            {
                bool pictures = sent.Paths.Count > 0 && sent.Paths.All(StorageReport.IsPicture);
                Keep(recipient.DeviceId, new ChatLastFile(sent.CreatedAt, true, sent.Recalled
                    ? RecalledText(pictures, sent.Paths.Count == 0, recipient.State == RecipientState.Pending)
                    : FileText(sent.Note, sent.Paths, pictures)));
            }
        }

        foreach (ReceivedOffer received in inbox.GetOffers().Where(o => o.GroupId is null && After(cleared, o.SenderId, o.ReceivedAt)))
        {
            string[] paths = [.. received.Files.Select(f => f.RelativePath)];
            bool pictures = received.Files.Count > 0 && received.Files.All(f => !f.IsDirectory && StorageReport.IsPicture(f.RelativePath));
            Keep(received.SenderId, new ChatLastFile(received.SentAt, false, received.Recalled ? RecalledText(pictures, paths.Length == 0) : FileText(received.Note, paths, pictures)));
        }

        return last;

        void Keep(DeviceId peer, ChatLastFile file)
        {
            // Contacts only: a conversation with anyone else cannot be written in.
            if (trust.Get(peer) is { TrustLevel: TrustLevel.Trusted } && (!last.TryGetValue(peer, out ChatLastFile? newest) || file.At > newest.At))
            {
                last[peer] = file;
            }
        }
    }

    /// <summary>The newest offer in each group's conversation, and who sent it, as its card shows there.</summary>
    private Dictionary<string, ChatLastFile> LastGroupFiles(IReadOnlyDictionary<string, DateTimeOffset> cleared)
    {
        Dictionary<string, ChatLastFile> last = [];
        foreach (SentOffer sent in offers.GetOffers().Where(o => o.GroupId is { } g && Shown(g, o.CreatedAt)))
        {
            bool pictures = sent.Paths.Count > 0 && sent.Paths.All(StorageReport.IsPicture);
            Keep(sent.GroupId!, new ChatLastFile(sent.CreatedAt, true, sent.Recalled
                ? RecalledText(pictures, sent.Paths.Count == 0, sent.Recipients.All(r => r.State == RecipientState.Pending))
                : FileText(sent.Note, sent.Paths, pictures)));
        }

        foreach (ReceivedOffer received in inbox.GetOffers().Where(o => o.GroupId is { } g && Shown(g, o.ReceivedAt)))
        {
            string[] paths = [.. received.Files.Select(f => f.RelativePath)];
            bool pictures = received.Files.Count > 0 && received.Files.All(f => !f.IsDirectory && StorageReport.IsPicture(f.RelativePath));
            Keep(received.GroupId!, new ChatLastFile(
                received.SentAt, false, received.Recalled ? RecalledText(pictures, paths.Length == 0) : FileText(received.Note, paths, pictures), received.SenderId));
        }

        return last;

        bool Shown(string groupId, DateTimeOffset at) => !cleared.TryGetValue(groupId, out DateTimeOffset clearedAt) || at > clearedAt;

        void Keep(string groupId, ChatLastFile file)
        {
            if (!last.TryGetValue(groupId, out ChatLastFile? newest) || file.At > newest.At)
            {
                last[groupId] = file;
            }
        }
    }

    /// <summary>What a file card taken back says, on both sides.</summary>
    /// <param name="text">Words alone ("Gửi văn bản"), no file.</param>
    /// <param name="cancelled">Ours, taken back before it reached anyone ("Hủy gửi"): they never saw it.</param>
    internal static string RecalledText(bool pictures, bool text = false, bool cancelled = false) =>
        cancelled ? L.T("Đã hủy gửi")
        : text ? L.T("Tin nhắn đã được thu hồi")
        : pictures ? L.T("Ảnh đã được thu hồi")
        : L.T("File đã được thu hồi");

    /// <summary>"Hủy gửi" while it has reached nobody (any time), "Thu hồi" once it has (within five minutes).</summary>
    internal static string RecallLabel(bool reachedNobody) => reachedNobody ? L.T("Hủy gửi") : L.T("Thu hồi");

    /// <summary>"[Ảnh]", "[3 ảnh]", the words of "Gửi văn bản", or "app.zip + 2 mục".</summary>
    private static string FileText(string? note, IReadOnlyList<string> paths, bool pictures) =>
        paths.Count == 0 ? string.IsNullOrWhiteSpace(note) ? NoContent : note
        : pictures ? paths.Count == 1 ? L.T("[Ảnh]") : L.F("[{0} ảnh]", paths.Count)
        : TransferText.Summary(paths);

    /// <summary>Text alone with no words: only in old data (docs/17-ui-cleanup.md §3).</summary>
    internal static string NoContent => L.T("(Không có nội dung)");

    /// <summary>Takes a message out of this conversation, here only.</summary>
    internal async Task DeleteAsync(ChatBubbleViewModel bubble)
    {
        if (Current is not { } person)
        {
            return;
        }

        if (playing?.Bubble == bubble)
        {
            StopPlaying();
        }

        if (person.GroupId is { } groupId)
        {
            await groupChat.DeleteAsync(groupId, [bubble.Id], CancellationToken.None);
        }
        else
        {
            await chat.DeleteAsync(person.Peer, [bubble.Id], CancellationToken.None);
        }
    }

    // ───────────── "Chọn nhiều": check messages, then delete them at once ─────────────

    /// <summary>A check box on each message, and the actions for the checked ones (docs/15-chat.md §6).</summary>
    [ObservableProperty]
    public partial bool IsSelecting { get; set; }

    private IEnumerable<ChatBubbleViewModel> Bubbles => Messages.OfType<ChatBubbleViewModel>();

    public int SelectedCount => Bubbles.Count(b => b.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectedText => L.F("Đã chọn {0}", SelectedCount);

    /// <summary>"Chọn tất cả": checked when all are, empty when none, in between otherwise.</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Bubbles.Count() ? true : null;
        set
        {
            bool select = SelectedCount < Bubbles.Count();
            foreach (ChatBubbleViewModel bubble in Bubbles)
            {
                bubble.IsSelected = select;
            }
        }
    }

    /// <summary>Called by a message whose check box changed, and when messages come or go while choosing.</summary>
    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>"Chọn nhiều" in a message's menu: choosing starts with that message checked.</summary>
    internal void StartSelecting(ChatBubbleViewModel first)
    {
        IsSelecting = true;
        first.IsSelected = true;
    }

    [RelayCommand]
    private void StopSelecting()
    {
        IsSelecting = false;
        foreach (ChatBubbleViewModel bubble in Bubbles)
        {
            bubble.IsSelected = false;
        }
    }

    /// <summary>The checked messages, here only; several are asked about first.</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        List<ChatBubbleViewModel> chosen = [.. Bubbles.Where(b => b.IsSelected)];
        if (Current is not { } person || chosen.Count == 0
            || (chosen.Count > 1 && Confirm is { } confirm && !await confirm(
                L.T("Xóa tin nhắn"),
                person.IsGroup
                    ? L.F("Xóa {0} tin nhắn trên máy này? Các thành viên khác vẫn giữ tin của họ.", chosen.Count)
                    : L.F("Xóa {0} tin nhắn trên máy này? Máy kia vẫn giữ tin của nó.", chosen.Count),
                L.T("Xóa"))))
        {
            return;
        }

        if (playing is { } now && chosen.Contains(now.Bubble))
        {
            StopPlaying();
        }

        StopSelecting();
        if (person.GroupId is { } groupId)
        {
            await groupChat.DeleteAsync(groupId, [.. chosen.Select(b => b.Id)], CancellationToken.None);
        }
        else
        {
            await chat.DeleteAsync(person.Peer, [.. chosen.Select(b => b.Id)], CancellationToken.None);
        }
    }

    /// <summary>"Xóa hội thoại…": every message and file card with this person, here only, after asking.</summary>
    [RelayCommand]
    private async Task DeleteConversationAsync()
    {
        if (Current is { GroupId: { } groupId } row)
        {
            await DeleteGroupConversationAsync(row, groupId);
            return;
        }

        if (Current is not { } person
            || (Confirm is { } confirm && !await confirm(
                L.T("Xóa hội thoại"),
                L.F("Xóa mọi tin nhắn với {0} trên máy này? Máy kia vẫn giữ tin của nó. File đã gửi, đã nhận vẫn ở trang File.", person.Name),
                L.T("Xóa"))))
        {
            return;
        }

        StopPlaying();
        await chat.DeleteConversationAsync(person.Peer, CancellationToken.None);
        // Someone no longer a contact has no conversation left to show: back to the list, where it is gone too.
        if (trust.GetLevel(person.Peer) != TrustLevel.Trusted && Current == person)
        {
            Current = null;
        }
    }

    /// <summary>The group's messages here; the members keep theirs. A group we are no longer in then leaves the list.</summary>
    private async Task DeleteGroupConversationAsync(ChatPersonViewModel row, string groupId)
    {
        if (Confirm is { } confirm && !await confirm(
                L.T("Xóa hội thoại"),
                L.F("Xóa mọi tin nhắn trong {0} trên máy này? Các thành viên khác vẫn giữ tin của họ.", row.Name),
                L.T("Xóa")))
        {
            return;
        }

        StopPlaying();
        await groupChat.DeleteConversationAsync(groupId, CancellationToken.None);
        if (groupChat.OthersIn(groupId) is null && Current == row)
        {
            Current = null;
        }
    }

    internal void Download(OfferKey key) => inbox.Download(key);

    /// <summary>"Thu hồi" on our message: a note on both sides from now on.</summary>
    internal async Task RecallAsync(ChatBubbleViewModel bubble)
    {
        if (Current is not { } person)
        {
            return;
        }

        if (playing?.Bubble == bubble)
        {
            StopPlaying();
        }

        if (person.GroupId is { } groupId)
        {
            await groupChat.RecallAsync(groupId, [bubble.Id], CancellationToken.None);
        }
        else
        {
            await chat.RecallAsync(person.Peer, [bubble.Id], CancellationToken.None);
        }
    }

    /// <summary>"Ngừng chia sẻ" on a file card we sent, past the time to take it back: the person can no longer download it.</summary>
    internal async Task WithdrawAsync(string offerId)
    {
        if (Confirm is null || await Confirm(
                L.T("Ngừng chia sẻ"), L.T("Ngừng chia sẻ file này? Người nhận sẽ không tải được nữa; ai đã tải xong thì vẫn giữ file."), L.T("Ngừng chia sẻ")))
        {
            await offers.WithdrawAsync(offerId);
        }
    }

    /// <summary>"Thu hồi" on a file card we sent: a note on both sides, and pictures that downloaded by themselves go.</summary>
    internal async Task RecallFileAsync(string offerId)
    {
        if (Current is { GroupId: { } groupId })
        {
            await groupChat.RecallOfferAsync(groupId, offerId, CancellationToken.None);
        }
        else if (Current is { } person)
        {
            await chat.RecallOfferAsync(person.Peer, offerId, CancellationToken.None);
        }
    }

    /// <summary>A picture of a card: the system's viewer, or Hộp nhận / Đã gửi where the viewer is not available.</summary>
    internal void OpenPicture(ChatPicture picture, bool received)
    {
        if (OpenFile is { } open && !(external?.Owns(picture.Location) ?? false))
        {
            open(picture.Location);
        }
        else
        {
            ShowFiles?.Invoke(received);
        }
    }

    /// <summary>Reads a picture where it is: a path, or a location of the OS such as content://.</summary>
    internal Stream OpenRead(string location) =>
        external?.Owns(location) == true ? external.OpenRead(location) : File.OpenRead(location);

    /// <summary>Names, online dots and whether the open conversation can be written to.</summary>
    private void RefreshPresence()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        foreach (ChatPersonViewModel person in Conversations)
        {
            if (person.GroupId is { } groupId)
            {
                person.Refresh(groups.Get(groupId), groupChat.OthersIn(groupId), live);
            }
            else
            {
                person.Refresh(trust.Get(person.Peer), live.GetValueOrDefault(person.Peer), NameOf(person.Peer));
            }
        }

        if (Current is not { } current)
        {
            CanWrite = false;
            Notice = null;
            return;
        }

        if (current.GroupId is { } open)
        {
            IReadOnlyList<GroupMember>? others = groupChat.OthersIn(open);
            bool hostAway = others is { Count: > 0 } && !groups.CanChat(open);
            CanWrite = others is { Count: > 0 } && !hostAway;
            Notice = others is null ? L.T("Bạn không còn trong nhóm này.")
                : others.Count == 0 ? L.T("Nhóm chưa có ai khác để nhắn.")
                : hostAway ? L.T("Host đang offline: nhóm này chỉ nhắn được khi Host online.")
                : null;
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

    private ChatPersonViewModel GroupRow(string groupId)
    {
        if (!groupRows.TryGetValue(groupId, out ChatPersonViewModel? row))
        {
            row = new ChatPersonViewModel(groupId, this);
            row.Refresh(groups.Get(groupId), groupChat.OthersIn(groupId), directory.Snapshot().ToDictionary(p => p.DeviceId));
            groupRows[groupId] = row;
        }

        return row;
    }

    private ChatPersonViewModel Person(DeviceId peer)
    {
        if (!people.TryGetValue(peer, out ChatPersonViewModel? person))
        {
            person = new ChatPersonViewModel(peer, this);
            person.Refresh(trust.Get(peer), directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer), NameOf(peer));
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

    /// <summary>"0:07", "4:59".</summary>
    internal static string Duration(int milliseconds) => $"{milliseconds / 60_000}:{milliseconds / 1000 % 60:00}";

    /// <summary>In the list: the time today, "Hôm qua", else the date.</summary>
    internal static string ShortTime(DateTimeOffset at)
    {
        DateTime local = at.ToLocalTime().DateTime;
        return local.Date == DateTime.Today ? local.ToString("HH:mm", L.Culture)
            : local.Date == DateTime.Today.AddDays(-1) ? L.T("Hôm qua")
            : local.ToString("dd/MM", L.Culture);
    }
}

/// <summary>A picture shown in a file card, and where it is, to open it.</summary>
public sealed record ChatPicture(Bitmap Image, string Location);

/// <summary>The newest offer with someone, for the conversation list: when, whose, and what it was.</summary>
/// <param name="Author">In a group, who sent it when not us.</param>
public sealed record ChatLastFile(DateTimeOffset At, bool Outgoing, string Text, DeviceId? Author = null);
