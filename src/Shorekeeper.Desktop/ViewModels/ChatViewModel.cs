using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// "Tin nhắn" (docs/15-chat.md §6), on the desktop and the phone: conversations, newest first, then contacts not
/// written to yet; the open one's messages and the box to write in. What the peer sent is read while the
/// conversation is shown (<see cref="IsShown"/>, set by the window). Voice messages are recorded with the microphone
/// and played one at a time (docs/15-chat.md §3). Files sent between the two show among the messages as cards (§6.3).
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ChatService chat;
    private readonly TrustStore trust;
    private readonly PeerDirectory directory;
    private readonly OfferService offers;
    private readonly InboxService inbox;
    private readonly SettingsService settings;
    private readonly IExternalFileSource? external;
    private readonly IMicrophone? microphone;
    private readonly IVoicePlayer? player;
    private readonly Dictionary<DeviceId, ChatPersonViewModel> people = [];
    private VoiceRecorder? recorder;
    private IDisposable? recording;
    private IDisposable? recordingTimer;
    private (ChatBubbleViewModel Bubble, VoiceNotePlayer Note, IDisposable Output, DispatcherTimer Timer)? playing;
    private bool filesQueued;

    public ChatViewModel(
        ChatService chat, TrustStore trust, PeerDirectory directory, OfferService offers, InboxService inbox, SettingsService settings,
        IExternalFileSource? external = null, IMicrophone? microphone = null, IVoicePlayer? player = null)
    {
        this.chat = chat;
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
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPresence);
        // Progress comes several times a second while files move: one refresh at a time is enough.
        offers.Changed += (_, _) => QueueFilesRefresh();
        inbox.Changed += (_, _) => QueueFilesRefresh();
        inbox.OfferArrived += (_, _) => QueueFilesRefresh();
        _ = RefreshAsync(null);
    }

    /// <summary>Puts text on the clipboard (set by the window).</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Opens a web address in the browser (set by the window).</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Asks before something that cannot be undone: title, question, action; true to go ahead (set by the window).</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

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

    /// <summary>The system's file picker for 📎 (set by the window); empty when nothing was picked.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>Sends files or folders to a contact as one offer (set by the window); a problem to show, or null.</summary>
    public Func<DeviceId, IReadOnlyList<string>, Task<string?>>? SendFiles { get; set; }

    /// <summary>
    /// Files can go to the open conversation's person: a contact. Unlike messages this does not need a version with
    /// chat, as files always went.
    /// </summary>
    public bool CanAttach => Current is { } person && SendFiles is not null && trust.GetLevel(person.Peer) == TrustLevel.Trusted;

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

    /// <summary>Files dropped or pasted into the conversation, or picked with 📎.</summary>
    public async Task AttachAsync(IReadOnlyList<string> paths)
    {
        if (CanAttach && Current is { } person && paths.Count > 0 && await SendFiles!(person.Peer, paths) is { } problem)
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

        await chat.SendVoiceAsync(person.Peer, note, CancellationToken.None);
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
        await chat.SendTextAsync(person.Peer, text, CancellationToken.None);
    }

    internal Task RetryAsync(ChatBubbleViewModel bubble) =>
        Current is { } person ? chat.RetryAsync(person.Peer, bubble.Id, CancellationToken.None) : Task.CompletedTask;

    partial void OnCurrentChanged(ChatPersonViewModel? oldValue, ChatPersonViewModel? newValue)
    {
        StopPlaying();
        IsSelecting = false;
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
        OnPropertyChanged(nameof(CanAttach));
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

        // Messages and file cards by time; a card's id is the offer's, which never looks like a message id.
        List<(DateTimeOffset At, string Id, object Source)> entries =
        [
            .. messages.Select(m => (m.SentAt, m.Id, (object)m)),
            .. offers.GetOffers()
                .Where(o => o.GroupId is null && o.Recipients.Any(r => r.DeviceId == person.Peer))
                .Select(o => (o.CreatedAt, "sent:" + o.OfferId, (object)o)),
            .. inbox.GetOffers().Where(o => o.SenderId == person.Peer).Select(o => (o.ReceivedAt, "received:" + o.OfferId, (object)o)),
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
                case (ChatFileViewModel card, SentOffer sent):
                    card.Update(sent, person.Peer);
                    // Only offers of pictures and nothing else show as pictures; by their names, as an Android source
                    // (content://…/document/msf:1234) need not have one.
                    card.ShowPictures(sent.Sources is { Count: > 0 } sources && sent.Paths.Count > 0 && sent.Paths.All(StorageReport.IsPicture) ? sources : []);
                    break;
                case (ChatFileViewModel card, ReceivedOffer received):
                    card.Update(received, person.IsOnline);
                    card.ShowPictures(
                        received.Files.Count > 0 && received.Files.All(f => !f.IsDirectory && StorageReport.IsPicture(f.RelativePath))
                            ? [.. received.Files.Where(f => f.State == InboxFileState.Completed && f.FinalPath is not null && File.Exists(f.FinalPath)).Select(f => f.FinalPath!)]
                            : []);
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

        if (added > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }

        if (IsShown && messages.Any(m => m.State == ChatMessageState.Unread))
        {
            await chat.MarkReadAsync(person.Peer, CancellationToken.None);
        }
    }

    private void QueueFilesRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (filesQueued || Current is not { } person)
        {
            return;
        }

        filesQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            filesQueued = false;
            if (Current == person)
            {
                await RefreshMessagesAsync(person);
            }
        });
    });

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

        await chat.DeleteAsync(person.Peer, [bubble.Id], CancellationToken.None);
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
                L.T("Xóa tin nhắn"), L.F("Xóa {0} tin nhắn trên máy này? Máy kia vẫn giữ tin của nó.", chosen.Count), L.T("Xóa"))))
        {
            return;
        }

        if (playing is { } now && chosen.Contains(now.Bubble))
        {
            StopPlaying();
        }

        StopSelecting();
        await chat.DeleteAsync(person.Peer, [.. chosen.Select(b => b.Id)], CancellationToken.None);
    }

    /// <summary>"Xóa hội thoại…": every message with this person, here only, after asking.</summary>
    [RelayCommand]
    private async Task DeleteConversationAsync()
    {
        if (Current is not { } person
            || (Confirm is { } confirm && !await confirm(
                L.T("Xóa hội thoại"),
                L.F("Xóa mọi tin nhắn với {0} trên máy này? Máy kia vẫn giữ tin của nó. File đã gửi, đã nhận vẫn ở Hộp nhận và Đã gửi.", person.Name),
                L.T("Xóa"))))
        {
            return;
        }

        StopPlaying();
        await chat.DeleteConversationAsync(person.Peer, CancellationToken.None);
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

        await chat.RecallAsync(person.Peer, [bubble.Id], CancellationToken.None);
    }

    /// <summary>"Thu hồi" on a file card we sent, as in Đã gửi: the person can no longer download it.</summary>
    internal async Task WithdrawAsync(string offerId)
    {
        if (Confirm is null || await Confirm(L.T("Thu hồi"), L.T("Thu hồi file này? Người nhận sẽ không tải được nữa; ai đã tải xong thì vẫn giữ file."), L.T("Thu hồi")))
        {
            await offers.WithdrawAsync(offerId);
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

/// <summary>Something in the open conversation, in time order: a message or a file card.</summary>
public abstract partial class ChatEntryViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>Set on the first entry of a day: "Hôm nay", "20/10/2026".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDay))]
    public partial string? DayText { get; set; }

    public bool HasDay => DayText is not null;
}

/// <summary>A message in the open conversation.</summary>
public sealed partial class ChatBubbleViewModel(string id, ChatViewModel owner) : ChatEntryViewModel(id)
{
    /// <summary>The kind last shown, so links and waveform are built again only when it changes.</summary>
    private string? kind;

    /// <summary>For the check box while choosing.</summary>
    public ChatViewModel Owner => owner;

    /// <summary>Checked in "Chọn nhiều".</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.SelectionChanged();

    [RelayCommand]
    private void SelectMany() => owner.StartSelecting(this);

    [ObservableProperty]
    public partial bool IsMine { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Web addresses in the text, each with its own button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinks))]
    public partial IReadOnlyList<string> Links { get; set; } = [];

    public bool HasLinks => Links.Count > 0;

    /// <summary>"14:05", with the state on ours: "14:05 · Đã xem".</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    /// <summary>A kind from a newer version: shown as a note, not as the sender's words.</summary>
    [ObservableProperty]
    public partial bool IsUnknown { get; set; }

    /// <summary>A voice message: ▶, the waveform and how long it plays instead of text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText))]
    public partial bool IsVoice { get; set; }

    public bool IsText => !IsVoice;

    [ObservableProperty]
    public partial string? VoicePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial int DurationMs { get; set; }

    public string DurationText => ChatViewModel.Duration(DurationMs);

    /// <summary>Heights of the waveform's bars, in pixels.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> Bars { get; set; } = [];

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>How far it has played, 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedWidth))]
    public partial double Progress { get; set; }

    /// <summary>Width of the bars already played (<see cref="WaveWidth"/> in all).</summary>
    public double PlayedWidth => Progress * WaveWidth;

    /// <summary>48 bars, 2 pixels wide with 2 between.</summary>
    public const double WaveWidth = (VoiceRecorder.WaveBars * 4) - 2;

    [RelayCommand]
    private Task RetryAsync() => owner.RetryAsync(this);

    [RelayCommand]
    private void Play() => owner.TogglePlay(this);

    /// <summary>Words to copy (not a voice message, nor the note left by a recall).</summary>
    [ObservableProperty]
    public partial bool CanCopy { get; set; }

    /// <summary>Ours and not taken back yet.</summary>
    [ObservableProperty]
    public partial bool CanRecall { get; set; }

    [RelayCommand]
    private void Copy() => owner.CopyText?.Invoke(Text);

    [RelayCommand]
    private Task RecallAsync() => owner.RecallAsync(this);

    [RelayCommand]
    private void OpenLink(string link) => owner.OpenLink?.Invoke(link);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);

    /// <param name="lastMine">Our newest message: it says how far the peer got even when delivered or read.</param>
    internal ChatBubbleViewModel Update(ChatMessage message, bool lastMine)
    {
        IsMine = message.Outgoing;
        IsVoice = message.Kind == ChatKinds.Voice;
        IsUnknown = message.Kind is not (ChatKinds.Text or ChatKinds.Voice);
        CanCopy = message.Kind == ChatKinds.Text;
        CanRecall = message.Outgoing && message.Kind is ChatKinds.Text or ChatKinds.Voice;
        Text = TextOf(message);
        // New lists only when the message changed (sent, recalled): every refresh would build the rows again.
        bool changed = message.Kind != kind;
        kind = message.Kind;
        if (changed)
        {
            Links = message.Kind == ChatKinds.Text ? TransferText.LinksIn(Text) : [];
        }
        VoicePath = message.VoicePath;
        DurationMs = message.DurationMs;
        if (changed)
        {
            Bars = IsVoice ? [.. Enumerable.Range(0, VoiceRecorder.WaveBars).Select(i => 3 + (21.0 * (message.Wave is { } wave && i < wave.Length ? wave[i] : 0) / 255))] : [];
        }
        IsFailed = message.State == ChatMessageState.Failed;
        string time = message.SentAt.ToLocalTime().ToString("HH:mm", L.Culture);
        string? state = !message.Outgoing || message.Kind == ChatKinds.Recalled ? null : message.State switch
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

    /// <summary>The words, or what stands for them in a list or a notification.</summary>
    internal static string TextOf(ChatMessage message) => message.Kind switch
    {
        ChatKinds.Text => message.Text ?? "",
        ChatKinds.Voice => L.F("Tin nhắn thoại {0}", ChatViewModel.Duration(message.DurationMs)),
        ChatKinds.Recalled => L.T("Tin nhắn đã được thu hồi"),
        _ => L.T("Tin nhắn này cần bản Shorekeeper mới hơn."),
    };
}

/// <summary>
/// Files sent between the two, shown among the messages (docs/15-chat.md §6.3): what, how big, how far along, [Tải] for
/// a new offer and [Xem] to see it in Hộp nhận or Đã gửi, where all it can do is.
/// </summary>
public sealed partial class ChatFileViewModel(string id, ChatViewModel owner) : ChatEntryViewModel(id)
{
    /// <summary>At most this many pictures of an offer show; the rest are "+N".</summary>
    private const int ShownPictures = 4;

    private OfferKey? key;
    private string? offerId;
    private string? picturesShown;

    /// <summary>Pictures of an offer of pictures, once they can be read here (sent: the originals; received: once downloaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPictures), nameof(IsPlain))]
    public partial IReadOnlyList<ChatPicture> Pictures { get; set; } = [];

    public bool HasPictures => Pictures.Count > 0;

    /// <summary>A card with its icon and name (no pictures to show).</summary>
    public bool IsPlain => !HasPictures;

    /// <summary>"+3" when more pictures came than show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial string? MoreText { get; set; }

    public bool HasMore => MoreText is not null;

    /// <summary>Ours and still open: it can be withdrawn.</summary>
    [ObservableProperty]
    public partial bool CanWithdraw { get; set; }

    [RelayCommand]
    private Task WithdrawAsync() => offerId is { } id ? owner.WithdrawAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private void OpenPicture(ChatPicture picture) => owner.OpenPicture(picture, received: !IsMine);

    /// <summary>Loads the pictures in the background when where they are has changed; none for a card of other files.</summary>
    internal void ShowPictures(IReadOnlyList<string> locations)
    {
        string shown = string.Join('\n', locations);
        if (shown == picturesShown)
        {
            return;
        }

        picturesShown = shown;
        MoreText = locations.Count > ShownPictures ? $"+{locations.Count - ShownPictures}" : null;
        if (locations.Count == 0)
        {
            Pictures = [];
            return;
        }

        _ = LoadAsync([.. locations.Take(ShownPictures)], shown);
    }

    private async Task LoadAsync(IReadOnlyList<string> locations, string shown)
    {
        List<ChatPicture> loaded = await Task.Run(() =>
        {
            List<ChatPicture> pictures = [];
            foreach (string location in locations)
            {
                try
                {
                    using Stream stream = owner.OpenRead(location);
                    pictures.Add(new ChatPicture(Bitmap.DecodeToWidth(stream, 480), location));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // Gone, or a kind the decoder does not know (HEIC): the card says what it is instead.
                }
            }

            return pictures;
        });
        if (picturesShown == shown)
        {
            Pictures = loaded;
        }
    }

    /// <summary>Sent by us: on the right, like our messages.</summary>
    [ObservableProperty]
    public partial bool IsMine { get; set; }

    /// <summary>"app.zip + 2 mục", or the words of "Gửi văn bản".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial bool IsText { get; set; }

    /// <summary>"12,4 MB · Đã nhận", "Bạn gửi · chưa mở".</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanDownload { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    public partial double Progress { get; set; } = -1;

    public bool IsDownloading => Progress >= 0;

    [RelayCommand]
    private void Download()
    {
        if (key is { } offer)
        {
            owner.Download(offer);
        }
    }

    [RelayCommand]
    private void Show() => owner.ShowFiles?.Invoke(!IsMine);

    internal void Update(SentOffer offer, DeviceId peer)
    {
        IsMine = true;
        key = null;
        offerId = offer.OfferId;
        CanWithdraw = offer.State == OfferState.Open;
        IsText = offer.Paths.Count == 0;
        Title = IsText ? TransferText.Preview(offer.Note ?? "", 200) : TransferText.Summary(offer.Paths);
        SentRecipient? recipient = offer.Recipients.FirstOrDefault(r => r.DeviceId == peer);
        string state = offer.State switch
        {
            OfferState.Withdrawn => L.T("Đã thu hồi"),
            OfferState.Expired => L.T("Đã hết hạn"),
            _ => recipient?.State switch
            {
                RecipientState.Pending => L.T("chờ gửi (đang offline)"),
                RecipientState.Delivered => L.T("chưa mở"),
                RecipientState.Downloading => L.T("đang tải"),
                RecipientState.Completed => L.T("đã nhận"),
                RecipientState.Partial => L.T("đã nhận một phần"),
                RecipientState.Declined => L.T("đã bỏ qua"),
                _ => L.T("lỗi"),
            },
        };
        StatusText = IsText ? state : $"{TransferText.Size(offer.TotalBytes)} · {state}";
        CanDownload = false;
        Progress = -1;
    }

    internal void Update(ReceivedOffer offer, bool senderOnline)
    {
        IsMine = false;
        key = new OfferKey(offer.SenderId, offer.OfferId);
        offerId = null;
        CanWithdraw = false;
        IsText = offer.Files.Count == 0;
        Title = IsText ? TransferText.Preview(offer.Note ?? "", 200) : TransferText.Summary(offer.Files.Select(f => f.RelativePath));
        string state = offer.State switch
        {
            InboxState.New when offer.ExpiresAt <= DateTimeOffset.UtcNow => L.T("Đã hết hạn"),
            InboxState.New => senderOnline ? L.T("Mới") : L.F("Người gửi offline · {0}", TransferText.Remaining(offer.ExpiresAt - DateTimeOffset.UtcNow)),
            InboxState.Downloading => L.F("Đang tải {0:0}%", offer.TotalBytes == 0 ? 0 : 100.0 * offer.BytesReceived / offer.TotalBytes),
            InboxState.Completed => L.T("Đã nhận"),
            InboxState.Partial => L.T("Đã tải một phần"),
            InboxState.Failed => L.F("Lỗi: {0}", offer.Error),
            InboxState.Cancelled => L.T("Đã hủy"),
            InboxState.Declined => L.T("Đã bỏ qua"),
            InboxState.Withdrawn => L.T("Người gửi đã thu hồi"),
            _ => L.T("Đã hết hạn"),
        };
        StatusText = IsText ? state : $"{TransferText.Size(offer.TotalBytes)} · {state}";
        CanDownload = offer.State == InboxState.New && senderOnline && offer.ExpiresAt > DateTimeOffset.UtcNow;
        Progress = offer.State == InboxState.Downloading && offer.TotalBytes > 0 ? 100.0 * offer.BytesReceived / offer.TotalBytes : -1;
    }
}

/// <summary>A picture shown in a file card, and where it is, to open it.</summary>
public sealed record ChatPicture(Bitmap Image, string Location);
