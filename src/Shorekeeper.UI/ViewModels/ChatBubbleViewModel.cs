using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;

namespace Shorekeeper.UI.ViewModels;

/// <summary>A message in the open conversation.</summary>
public sealed partial class ChatBubbleViewModel(string id, ChatViewModel owner) : ChatEntryViewModel(id)
{
    /// <summary>The kind last shown, so links and waveform are built again only when it changes.</summary>
    private string? kind;

    /// <summary>Until when "Thu hồi" is offered: never for theirs, any time for one still waiting.</summary>
    private DateTimeOffset recallUntil;

    /// <summary>Until when "Sửa" is offered: ours in words only, any time for one not delivered yet.</summary>
    private DateTimeOffset editUntil;

    /// <summary>For the check box while choosing.</summary>
    public ChatViewModel Owner => owner;

    /// <summary>Checked in "Chọn nhiều".</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.SelectionChanged();

    [RelayCommand]
    private void SelectMany() => owner.StartSelecting(this);

    /// <summary>What this answers: a message's id, or "offer:" and a file card's offer (docs/18-chat-extras.md §4).</summary>
    public string? ReplyTo { get; private set; }

    /// <summary>The message answered shows above the words; clicked, the conversation goes to it.</summary>
    [ObservableProperty]
    public partial bool HasQuote { get; set; }

    [ObservableProperty]
    public partial string QuoteAuthor { get; set; } = "";

    [ObservableProperty]
    public partial string QuoteText { get; set; } = "";

    /// <summary>A member's message that names us, or everyone: marked.</summary>
    [ObservableProperty]
    public partial bool MentionsMe { get; set; }

    /// <summary>Words or a voice message, not one taken back.</summary>
    [ObservableProperty]
    public partial bool CanReply { get; set; }

    [RelayCommand]
    private void Reply() => owner.StartReply(this);

    [RelayCommand]
    private void ShowQuote() => owner.ShowQuote(this);

    /// <summary>The reactions under the words, one chip each (docs/18-chat-extras.md §6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    public partial IReadOnlyList<ChatReactionChip> Reactions { get; set; } = [];

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>Ours on it: choosing it again takes it away.</summary>
    public string? MyReaction { get; private set; }

    /// <summary>What can be chosen: 👍 ❤️ 😂 😮 😢 🙏.</summary>
    public static IReadOnlyList<string> Emojis => ChatReactions.Emojis;

    [RelayCommand]
    private Task ReactAsync(string emoji) => owner.ReactAsync(this, emoji == MyReaction ? null : emoji);

    /// <summary>A chip clicked or tapped: who chose it.</summary>
    [RelayCommand]
    private void ShowReactors(ChatReactionChip chip) => owner.Notice = chip.Tip;

    internal void ShowReactions(IReadOnlyList<ChatReactionChip> chips, string? mine)
    {
        MyReaction = mine;
        if (!chips.SequenceEqual(Reactions))
        {
            Reactions = chips;
        }
    }

    /// <summary>The chips for a message's reactions, in the order of the set, each with who chose it.</summary>
    internal static IReadOnlyList<ChatReactionChip> Chips(IEnumerable<(string Name, string Emoji, bool Mine)> reactions) =>
    [
        .. reactions.GroupBy(r => r.Emoji)
            .OrderBy(g => ChatReactions.Emojis.ToList().IndexOf(g.Key))
            .Select(g => new ChatReactionChip(g.Key, g.Count(), g.Any(r => r.Mine), string.Join(", ", g.Select(r => r.Name)))),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatarSpace), nameof(CanShowDetails))]
    public partial bool IsMine { get; set; }

    /// <summary>In a group's conversation: others' messages keep room on the left for the member's initial.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatarSpace), nameof(CanShowDetails))]
    public partial bool IsInGroup { get; set; }

    public bool HasAvatarSpace => IsInGroup && !IsMine;

    /// <summary>The first of a run of one member's messages: their name above it and their initial beside it.</summary>
    [ObservableProperty]
    public partial bool ShowsAuthor { get; set; }

    [ObservableProperty]
    public partial string AuthorName { get; set; } = "";

    [ObservableProperty]
    public partial string AuthorInitial { get; set; } = "";

    /// <summary>Each member keeps one color, from their DeviceId.</summary>
    [ObservableProperty]
    public partial IBrush? AuthorBrush { get; set; }

    /// <summary>Ours in a group: "Chi tiết" lists where it is with each member.</summary>
    public bool CanShowDetails => IsInGroup && IsMine;

    [RelayCommand]
    private Task ShowDetailsAsync() => owner.ShowDetailsAsync(this);

    /// <summary>A member's color, the same on every device: from their DeviceId.</summary>
    internal static IBrush BrushOf(DeviceId member) =>
        AuthorBrushes[(int)((uint)member.Value.Aggregate(17, (h, c) => (h * 31) + c) % AuthorBrushes.Length)];

    private static readonly IBrush[] AuthorBrushes =
    [
        .. ((string[])["#E11D48", "#D97706", "#059669", "#0284C7", "#7C3AED", "#DB2777", "#0D9488", "#65A30D"])
            .Select(hex => (IBrush)new ImmutableSolidColorBrush(Color.Parse(hex))),
    ];

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

    /// <summary>A member's message that came while they were out of the group (docs/16-group-chat.md §3.6): kept, with a note.</summary>
    [ObservableProperty]
    public partial bool IsOutOfGroup { get; set; }

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

    /// <summary>Ours, not taken back yet, and sent less than five minutes ago (or still waiting).</summary>
    [ObservableProperty]
    public partial bool CanRecall { get; set; }

    [RelayCommand]
    private void Copy() => owner.CopyText?.Invoke(Text);

    [RelayCommand]
    private Task RecallAsync() => owner.RecallAsync(this);

    /// <summary>Ours in words, sent less than fifteen minutes ago or not delivered yet (docs/18-chat-extras.md §5).</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; set; }

    [RelayCommand]
    private void Edit() => owner.StartEdit(this);

    internal override void RefreshRecall()
    {
        CanRecall = DateTimeOffset.UtcNow <= recallUntil;
        CanEdit = DateTimeOffset.UtcNow <= editUntil;
    }

    [RelayCommand]
    private void OpenLink(string link) => owner.OpenLink?.Invoke(link);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);

    /// <param name="lastMine">Our newest message: it says how far the peer got even when delivered or read.</param>
    internal ChatBubbleViewModel Update(ChatMessage message, bool lastMine)
    {
        string? state = !message.Outgoing || message.Kind == ChatKinds.Recalled ? null : message.State switch
        {
            ChatMessageState.Pending => L.T("Chờ gửi"),
            ChatMessageState.Failed => L.T("Không gửi được"),
            ChatMessageState.Delivered when lastMine => L.T("Đã nhận"),
            ChatMessageState.Read when lastMine => L.T("Đã xem"),
            _ => null,
        };
        ReplyTo = message.ReplyTo;
        Show(
            message.Outgoing, message.Kind, TextOf(message), message.SentAt, message.State, message.DurationMs, message.Wave, message.VoicePath, state,
            message.EditedAt is not null);
        return this;
    }

    /// <param name="author">"Bạn", or the member's name.</param>
    /// <param name="first">The first of a run of this member's messages.</param>
    /// <param name="reached">Our newest message: where it is with each member.</param>
    internal ChatBubbleViewModel Update(GroupChatMessage message, string author, bool first, IReadOnlyList<GroupDelivery>? reached)
    {
        IsInGroup = true;
        AuthorName = author;
        AuthorInitial = author.Length > 0 ? author[..1].ToUpper(L.Culture) : "?";
        AuthorBrush = BrushOf(message.Author);
        ShowsAuthor = !message.Outgoing && first;
        IsOutOfGroup = message.OutOfGroup;
        ReplyTo = message.ReplyTo;
        string? state = !message.Outgoing || message.Kind == ChatKinds.Recalled ? null
            : message.State == ChatMessageState.Pending ? L.T("Chờ gửi")
            : message.State == ChatMessageState.Failed ? L.T("Không gửi được")
            : reached is { Count: > 0 } all
                ? all.Any(d => d.State == GroupDeliveryState.Read)
                    ? L.F("Đã xem {0}/{1}", all.Count(d => d.State == GroupDeliveryState.Read), all.Count)
                    : L.F("Đã nhận {0}/{1}", all.Count(d => d.State == GroupDeliveryState.Delivered), all.Count)
                : null;
        Show(message.Outgoing, message.Kind, TextOf(message.Kind, message.Text, message.DurationMs, message.Outgoing && message.State == ChatMessageState.Pending),
            message.SentAt, message.State, message.DurationMs, message.Wave, message.VoicePath, state, message.EditedAt is not null);
        return this;
    }

    /// <param name="edited">Its words changed after it reached someone: "Đã sửa" by the time.</param>
    private void Show(
        bool outgoing, string messageKind, string text, DateTimeOffset sentAt, ChatMessageState messageState, int durationMs, byte[]? wave,
        string? voicePath, string? state, bool edited)
    {
        IsMine = outgoing;
        IsVoice = messageKind == ChatKinds.Voice;
        IsUnknown = messageKind is not (ChatKinds.Text or ChatKinds.Voice);
        CanCopy = messageKind == ChatKinds.Text;
        CanReply = messageKind is ChatKinds.Text or ChatKinds.Voice;
        recallUntil = !outgoing || messageKind is not (ChatKinds.Text or ChatKinds.Voice) ? DateTimeOffset.MinValue
            : messageState == ChatMessageState.Pending ? DateTimeOffset.MaxValue
            : sentAt + ChatService.RecallWindow;
        editUntil = !outgoing || messageKind != ChatKinds.Text ? DateTimeOffset.MinValue
            : messageState is ChatMessageState.Pending or ChatMessageState.Failed ? DateTimeOffset.MaxValue
            : sentAt + ChatService.EditWindow;
        RecallText = ChatViewModel.RecallLabel(messageState == ChatMessageState.Pending);
        RefreshRecall();
        // New lists only when the message changed (sent, recalled, edited): every refresh would build the rows again.
        bool newWords = text != Text;
        Text = text;
        bool changed = messageKind != kind;
        kind = messageKind;
        if (changed || newWords)
        {
            Links = messageKind == ChatKinds.Text ? TransferText.LinksIn(Text) : [];
        }
        VoicePath = voicePath;
        DurationMs = durationMs;
        if (changed)
        {
            Bars = IsVoice ? [.. Enumerable.Range(0, VoiceRecorder.WaveBars).Select(i => 3 + (21.0 * (wave is not null && i < wave.Length ? wave[i] : 0) / 255))] : [];
        }
        IsFailed = messageState == ChatMessageState.Failed;
        string time = sentAt.ToLocalTime().ToString("HH:mm", L.Culture);
        time = edited ? $"{time} · {L.T("Đã sửa")}" : time;
        StatusText = state is null ? time : $"{time} · {state}";
    }

    /// <summary>The words, or what stands for them in a list or a notification.</summary>
    internal static string TextOf(ChatMessage message) =>
        TextOf(message.Kind, message.Text, message.DurationMs, message.Outgoing && message.State == ChatMessageState.Pending);

    /// <param name="cancelled">Ours, taken back while still waiting to be sent: it never left ("Đã hủy gửi").</param>
    internal static string TextOf(string kind, string? text, int durationMs, bool cancelled = false) => kind switch
    {
        ChatKinds.Text => text ?? "",
        ChatKinds.Voice => L.F("Tin nhắn thoại {0}", ChatViewModel.Duration(durationMs)),
        ChatKinds.Recalled => cancelled ? L.T("Đã hủy gửi") : L.T("Tin nhắn đã được thu hồi"),
        _ => L.T("Tin nhắn này cần bản Shorekeeper mới hơn."),
    };
}
