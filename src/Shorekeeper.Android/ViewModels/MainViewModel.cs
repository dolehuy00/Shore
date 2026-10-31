using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;
using GeneratedRegexAttribute = System.Text.RegularExpressions.GeneratedRegexAttribute;
using Regex = System.Text.RegularExpressions.Regex;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// The phone's one screen (docs/14-android.md, A2–A5): "Xóm" (contacts and who is online, Kết nối, Gửi file),
/// "File" (Nhận: download into Download/Shorekeeper; Đã gửi), "Tin nhắn", "Nhóm", "Âm thanh" and "Cài đặt". Connect
/// requests and the recipients of a share show as overlays.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly PairingService pairing;
    private readonly InboxService inbox;
    private readonly OfferService offers;
    private readonly IExternalFileSource external;
    private readonly GroupStore groups;
    private readonly SettingsService settings;
    private readonly PeerNaming naming;
    private readonly LocalDevice device;
    private readonly AppPaths paths;
    private DispatcherTimer? noticeTimer;
    private readonly ManualPeerFinder finder;
    private readonly DispatcherTimer progressTimer;

    /// <param name="groupsTab">Nhóm, and <paramref name="settingsTab"/> Cài đặt: made by the container for this view model.</param>
    public MainViewModel(
        PeerDirectory directory, TrustStore trust, PairingService pairing, InboxService inbox, OfferService offers, IExternalFileSource external,
        GroupStore groups, SettingsService settings, PeerNaming naming, LocalDevice device, AppPaths paths, ManualPeerFinder finder,
        ChatViewModel chat, AudioViewModel audio, Func<MainViewModel, GroupsTabViewModel> groupsTab,
        Func<MainViewModel, SettingsTabViewModel> settingsTab)
    {
        this.directory = directory;
        this.trust = trust;
        this.pairing = pairing;
        this.inbox = inbox;
        this.offers = offers;
        this.external = external;
        this.groups = groups;
        this.settings = settings;
        // Before the tabs: Nhóm names its members while it is being made, before Chat is set.
        this.naming = naming;
        this.device = device;
        this.paths = paths;
        this.finder = finder;
        Groups = groupsTab(this);
        Audio = audio;
        Audio.AllowPlaying = () => MainActivity.Current is { } activity
            ? PlaybackCapture.RequestAsync(activity, settings.Current.AudioFromMicrophone)
            : Task.FromResult(false);
        Audio.CanChooseSource = true;
        Audio.Notice += (_, message) => Notice = message;
        Audio.AskPassword = AskPasswordAsync;
        Settings = settingsTab(this);
        Chat = chat;
        Chat.AllowRecording = AllowRecordingAsync;
        Chat.ShowsGroups = true;
        Chat.CopyText = text =>
        {
            TextActions.Copy(global::Android.App.Application.Context, text);
            Notice = L.T("Đã chép");
        };
        Chat.OpenLink = link => TextActions.Open(global::Android.App.Application.Context, link);
        Chat.AskText = AskTextAsync;
        Chat.Confirm = (title, question, action) =>
        {
            var answer = new TaskCompletionSource<bool>();
            Question = new QuestionViewModel(title, question, action, null, _ =>
            {
                answer.TrySetResult(true);
                return Task.CompletedTask;
            }, this)
            {
                Cancelled = () => answer.TrySetResult(false),
            };
            return answer.Task;
        };
        Chat.ShowFiles = received =>
        {
            FilesShowSent = !received;
            SelectedTab = 1;
        };
        Chat.SendFiles = (recipients, groupId, locations) => SendAsync(locations, [.. recipients.Select(r => (r, groupId))], showSent: false);
        OfferSelection = new ListSelection(() => Offers, () => RemoveCheckedOffers(deleteFiles: false), () => RemoveCheckedOffers(deleteFiles: true));
        SentSelection = new ListSelection(() => Sent, RemoveCheckedSent);
        settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Title)));
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(HasPageTitle));
            }
        };

        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        inbox.OfferArrived += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        inbox.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        offers.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshSent);
        pairing.IncomingRequested += (_, request) => Dispatcher.UIThread.Post(() => Incoming = new IncomingPairingViewModel(request, this));
        pairing.IncomingClosed += (_, id) => Dispatcher.UIThread.Post(() =>
        {
            if (Incoming?.Id == id)
            {
                Incoming = null;
            }
        });

        // Progress is read from the engine once a second while something downloads.
        progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            RefreshOffers();
            RefreshSent();
        });
        RefreshPeers();
        RefreshOffers();
        RefreshSent();
    }

    public string Title => device.DisplayName;

    public string Subtitle => L.F("{0} · nhận vào {1}", device.DeviceId.ShortForm, SharedStoragePrefix().Replace(paths.DefaultDownloadDirectory, ""));

    public GroupsTabViewModel Groups { get; }

    public AudioViewModel Audio { get; }

    public SettingsTabViewModel Settings { get; }

    /// <summary>"Tin nhắn".</summary>
    public ChatViewModel Chat { get; }

    public ObservableCollection<PeerItemViewModel> Peers { get; } = [];

    public ObservableCollection<OfferItemViewModel> Offers { get; } = [];

    public ObservableCollection<SentItemViewModel> Sent { get; } = [];

    public bool HasNoSent => Sent.Count == 0;

    /// <summary>"Chọn" on Hộp nhận.</summary>
    public ListSelection OfferSelection { get; }

    /// <summary>"Chọn" on Đã gửi.</summary>
    public ListSelection SentSelection { get; }

    /// <summary>0 Xóm, 1 File (Nhận or Đã gửi, <see cref="FilesShowSent"/>), 2 Tin nhắn, 3 Nhóm, 4 Âm thanh, 5 Cài đặt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNearbyTab), nameof(IsFilesTab), nameof(IsInboxTab), nameof(IsSentTab), nameof(IsChatTab), nameof(IsGroupsTab),
        nameof(IsAudioTab), nameof(IsSettingsTab), nameof(PageTitle), nameof(HasPageTitle))]
    public partial int SelectedTab { get; set; }

    /// <summary>"Đã gửi" rather than "Nhận" on the File tab (docs/15-chat.md §6.2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInboxTab), nameof(IsSentTab))]
    public partial bool FilesShowSent { get; set; }

    /// <summary>The app is on screen (resumed): with "Tin nhắn" shown, what arrives there is read.</summary>
    [ObservableProperty]
    public partial bool IsInForeground { get; set; }

    public bool IsNearbyTab => SelectedTab == 0;

    public bool IsFilesTab => SelectedTab == 1;

    public bool IsInboxTab => SelectedTab == 1 && !FilesShowSent;

    public bool IsSentTab => SelectedTab == 1 && FilesShowSent;

    public bool IsChatTab => SelectedTab == 2;

    public bool IsGroupsTab => SelectedTab == 3;

    public bool IsAudioTab => SelectedTab == 4;

    public bool IsSettingsTab => SelectedTab == 5;

    /// <summary>The tab's name, large above its page; Cài đặt has this phone's name instead.</summary>
    public string PageTitle => SelectedTab switch
    {
        0 => L.T("Xóm"),
        1 => "File",
        2 => L.T("Tin nhắn"),
        3 => L.T("Nhóm"),
        4 => L.T("Âm thanh"),
        _ => "",
    };

    /// <summary>Not in Cài đặt, nor over an open conversation, which has its own bar (← name).</summary>
    public bool HasPageTitle => !IsSettingsTab && !(IsChatTab && Chat.IsOpen);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShare))]
    public partial ShareViewModel? Share { get; set; }

    public bool HasShare => Share is not null;

    /// <summary>A short message at the bottom; it goes away by itself after a few seconds, or with its ✕.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    partial void OnNoticeChanged(string? value)
    {
        noticeTimer?.Stop();
        if (value is not null)
        {
            noticeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) => Notice = null);
            noticeTimer.Start();
        }
    }

    [RelayCommand]
    private void CloseNotice() => Notice = null;

    public bool HasNoPeers => Peers.Count == 0;

    public bool HasNoOffers => Offers.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming))]
    public partial IncomingPairingViewModel? Incoming { get; set; }

    public bool HasIncoming => Incoming is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutgoing))]
    public partial OutgoingPairingViewModel? Outgoing { get; set; }

    public bool HasOutgoing => Outgoing is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestion))]
    public partial QuestionViewModel? Question { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddPeer))]
    public partial AddPeerViewModel? AddPeer { get; set; }

    public bool HasAddPeer => AddPeer is not null;

    /// <summary>The + on Xóm.</summary>
    [RelayCommand]
    private void OpenAddPeer() => AddPeer = new AddPeerViewModel(finder, this);

    public bool HasQuestion => Question is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoAccept))]
    public partial AutoAcceptChoiceViewModel? AutoAccept { get; set; }

    public bool HasAutoAccept => AutoAccept is not null;

    /// <summary>"Tự nhận file…" on a contact: off, the limit in Settings, or its own.</summary>
    internal void ChooseAutoAccept(PeerItemViewModel peer) =>
        AutoAccept = new AutoAcceptChoiceViewModel(
            peer.Name, trust.Get(peer.DeviceId)?.AutoAcceptMaxBytes, settings.Current.AutoAcceptLimitGb,
            maxBytes => trust.SetAutoAcceptAsync(peer.DeviceId, maxBytes), () => AutoAccept = null);

    internal void AskText(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.T("Gửi văn bản"), L.F("Văn bản hoặc link gửi cho {0}:", peer.Name), L.T("Gửi"),
            TextActions.Copied(global::Android.App.Application.Context),
            async text =>
            {
                if (!string.IsNullOrWhiteSpace(text) && await SendAsync([], [(peer.DeviceId, null)], text) is { } error)
                {
                    Notice = error;
                }
            }, this);

    private Task<string?> AskPasswordAsync(PasswordQuestion question)
    {
        var answer = new TaskCompletionSource<string?>();
        Question = new QuestionViewModel(question.Title, question.Label, question.OkText, "", text =>
        {
            answer.TrySetResult(text);
            return Task.CompletedTask;
        }, this)
        {
            IsPassword = question.Masked,
            Cancelled = () => answer.TrySetResult(null),
        };
        return answer.Task;
    }

    private Task<string?> AskTextAsync(string title, string label, string text)
    {
        var answer = new TaskCompletionSource<string?>();
        Question = new QuestionViewModel(title, label, L.T("Lưu"), text, typed =>
        {
            answer.TrySetResult(typed);
            return Task.CompletedTask;
        }, this)
        {
            Cancelled = () => answer.TrySetResult(null),
        };
        return answer.Task;
    }

    internal void AskAlias(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.T("Đặt tên gợi nhớ"), L.F("Tên gợi nhớ cho {0}:", peer.Host), L.T("Lưu"), peer.Name,
            alias => trust.SetAliasAsync(peer.DeviceId, alias), this);

    internal void ConfirmDisconnect(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.F("Ngắt kết nối với {0}?", peer.Name), L.T("Muốn gửi file cho nhau lại thì phải Kết nối lại."), L.T("Ngắt kết nối"), null,
            _ => pairing.DisconnectAsync(peer.DeviceId), this);

    internal void ConfirmBlock(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.F("Chặn {0}?", peer.Name),
            L.T("Máy này sẽ không gửi được yêu cầu kết nối hay file cho bạn nữa. Bỏ chặn trong Cài đặt."), L.T("Chặn"), null,
            _ => trust.BlockAsync(peer.DeviceId, peer.Name, peer.Host), this);

    internal string NameOf(DeviceId id) => naming.Of(id);

    /// <param name="fallback">The name to use when nothing else knows it, e.g. its name in a group.</param>
    internal string NameOf(DeviceId id, string fallback) => naming.Of(id, fallback);

    /// <param name="invite">From the computer's QR code: it connects without the code being compared.</param>
    internal async Task ConnectAsync(DeviceId peer, string name, string? invite = null)
    {
        var outgoing = new OutgoingPairingViewModel(name, this);
        Outgoing = outgoing;
        PairingOutcome outcome;
        try
        {
            outcome = await pairing.RequestAsync(
                peer, null, pending => Dispatcher.UIThread.Post(() => outgoing.Code = Format(pending.Code)), outgoing.Cancellation.Token, invite);
        }
        catch (Exception ex) when (ex is Engine.Api.PeerUnreachableException or Engine.Api.PeerApiException or HttpRequestException)
        {
            outgoing.Finish(L.F("Không liên lạc được với {0}.", name));
            return;
        }
        catch (OperationCanceledException)
        {
            Outgoing = null;
            return;
        }

        outgoing.Finish(outcome switch
        {
            PairingOutcome.Accepted => L.F("Đã kết nối với {0}.", name),
            PairingOutcome.Declined => L.F("{0} đã từ chối.", name),
            PairingOutcome.Expired => L.T("Yêu cầu đã hết hạn."),
            _ => L.T("Đã hủy."),
        });
    }

    internal void CloseOutgoing() => Outgoing = null;

    internal void Accept(IncomingPairingViewModel request)
    {
        pairing.Accept(request.Id);
        Incoming = null;
    }

    internal void Decline(IncomingPairingViewModel request)
    {
        pairing.Decline(request.Id);
        Incoming = null;
    }

    /// <summary>Items from the share sheet: ask whom to send them to — contacts, and fellow group members through their group.</summary>
    public void ShowShare(IReadOnlyList<string> locations, string? text = null)
    {
        IReadOnlyList<PeerInfo> live = directory.Snapshot();
        List<PickablePerson> people =
        [
            .. trust.GetAll(TrustLevel.Trusted)
                .Select(record =>
                {
                    PeerInfo? peer = live.FirstOrDefault(p => p.DeviceId == record.DeviceId);
                    return new PickablePerson(record.DeviceId, record.ShownName, Detail(peer, record.HostName), null, peer);
                }),
        ];
        if (settings.Current.GroupsEnabled)
        {
            foreach (Group group in groups.GetAll().Where(g => g.Role != GroupRole.Pending))
            {
                foreach (GroupMember member in group.Members.Where(m => m.DeviceId != device.DeviceId
                    && trust.GetLevel(m.DeviceId) == TrustLevel.Unknown && people.TrueForAll(p => p.DeviceId != m.DeviceId)))
                {
                    PeerInfo? peer = live.FirstOrDefault(p => p.DeviceId == member.DeviceId);
                    people.Add(new PickablePerson(member.DeviceId, peer?.DisplayName ?? member.Name, L.F("{0} · nhóm {1}", Detail(peer, member.Name), group.Name), group.Id, peer));
                }
            }
        }

        Share = new ShareViewModel(locations, text, [.. people.OrderBy(p => !p.IsOnline).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)], this);
    }

    internal void CloseShare() => Share = null;

    /// <summary>
    /// Sends files picked in the app or shared from another app; locations may be paths or content:// URIs.
    /// One offer for contacts, one per group for fellow members who are not contacts (docs/07-groups.md §4).
    /// </summary>
    /// <param name="text">"Gửi văn bản": sent alone when there are no files, as the offer's note with them.</param>
    /// <param name="showSent">Show File → Đã gửi afterwards; not from a conversation, where the file shows as a card.</param>
    internal async Task<string?> SendAsync(
        IReadOnlyList<string> locations, IReadOnlyList<(DeviceId Id, string? GroupId)> recipients, string? text = null, bool showSent = true)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (text?.Length > OfferService.MaxNoteLength)
        {
            return L.F("Văn bản dài quá {0} ký tự.", OfferService.MaxNoteLength.ToString("N0", L.Culture));
        }

        if (locations.Count == 0)
        {
            if (text is not null)
            {
                var batches = recipients.GroupBy(r => r.GroupId).ToList();
                foreach (var batch in batches)
                {
                    await offers.CreateAsync(new PreparedOffer([], []), [.. batch.Select(r => r.Id)], text, snapshotLocked: false, groupId: batch.Key, CancellationToken.None);
                }

                // Text shows in the conversation, not in File (docs/17-ui-cleanup.md §2): one person or one group, open it.
                if (showSent && batches is [{ } only])
                {
                    if (only.Key is { } groupId)
                    {
                        OpenGroupChat(groupId);
                    }
                    else if (only.Count() == 1)
                    {
                        OpenChat(only.First().Id);
                    }
                }
            }

            return null;
        }

        PreparedOffer prepared;
        try
        {
            prepared = await Task.Run(() => OfferService.Prepare(locations, external));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        if (prepared.Files.Count == 0)
        {
            return L.T("Không đọc được file nào trong số đã chọn.");
        }

        foreach (var batch in recipients.GroupBy(r => r.GroupId))
        {
            await offers.CreateAsync(prepared, [.. batch.Select(r => r.Id)], text, snapshotLocked: false, groupId: batch.Key, CancellationToken.None);
        }

        if (showSent)
        {
            ShowSentFiles();
        }

        return prepared.Unreadable.Count > 0 ? L.F("{0} mục không đọc được đã bị bỏ qua.", prepared.Unreadable.Count) : null;
    }

    internal Task WithdrawAsync(SentItemViewModel offer) => offers.WithdrawAsync(offer.OfferId);

    // ───────────── "Xóa" (docs/10-ux.md §4) ─────────────

    /// <summary>How long a removed card takes to fade away (Themes/Motion.axaml).</summary>
    private static readonly TimeSpan LeaveTime = TimeSpan.FromMilliseconds(200);

    private void RemoveCheckedOffers(bool deleteFiles) => RemoveOffers(OfferSelection.Selected<OfferItemViewModel>(), deleteFiles);

    private void RemoveCheckedSent() => RemoveSent(SentSelection.Selected<SentItemViewModel>());

    /// <summary>
    /// Takes offers out of Hộp nhận, with their received files when asked. Deleting files, or several offers at once, is
    /// asked first; deleted files are gone for good.
    /// </summary>
    internal void RemoveOffers(IReadOnlyList<OfferItemViewModel> chosen, bool deleteFiles)
    {
        if (chosen.Count == 0)
        {
            return;
        }

        async Task RemoveAsync()
        {
            int downloading = chosen.Count(o => o.IsDownloading);
            foreach (OfferItemViewModel offer in chosen.Where(o => !o.IsDownloading))
            {
                offer.IsLeaving = true;
            }

            await Task.Delay(LeaveTime);
            (_, int kept) = await inbox.RemoveAsync([.. chosen.Select(o => o.Key)], deleteFiles);
            OfferSelection.Stop();
            RefreshOffers();
            if (downloading > 0)
            {
                Notice = L.F("{0} mục đang tải nên chưa xóa: hủy tải trước.", downloading);
            }
            else if (kept > 0)
            {
                Notice = L.F("Không xóa được {0} file (có thể đang mở trong app khác).", kept);
            }
        }

        if (!deleteFiles && chosen.Count == 1)
        {
            _ = RemoveAsync();
            return;
        }

        Question = new QuestionViewModel(
            L.T("Xóa"),
            deleteFiles
                ? L.F("Xóa {0} mục khỏi danh sách đã nhận và xóa luôn file đã nhận của chúng? File bị xóa hẳn khỏi máy.", chosen.Count)
                : L.F("Xóa {0} mục khỏi danh sách đã nhận? File đã nhận vẫn giữ nguyên.", chosen.Count),
            L.T("Xóa"), null, _ => RemoveAsync(), this);
    }

    /// <summary>Takes offers out of Đã gửi; the files sent stay. Asked first for several offers, or one still open.</summary>
    internal void RemoveSent(IReadOnlyList<SentItemViewModel> chosen)
    {
        if (chosen.Count == 0)
        {
            return;
        }

        async Task RemoveAsync()
        {
            foreach (SentItemViewModel offer in chosen)
            {
                offer.IsLeaving = true;
            }

            await Task.Delay(LeaveTime);
            await offers.RemoveAsync([.. chosen.Select(o => o.OfferId)]);
            SentSelection.Stop();
            RefreshSent();
        }

        if (chosen.Count == 1 && !chosen[0].CanWithdraw)
        {
            _ = RemoveAsync();
            return;
        }

        Question = new QuestionViewModel(
            L.T("Xóa"),
            chosen.Any(o => o.CanWithdraw)
                ? L.F("Xóa {0} mục khỏi danh sách đã gửi? Mục còn đang mở sẽ bị thu hồi: người nhận chưa tải sẽ không tải được nữa. File gốc của bạn vẫn giữ nguyên.", chosen.Count)
                : L.F("Xóa {0} mục khỏi danh sách đã gửi? File gốc của bạn vẫn giữ nguyên.", chosen.Count),
            L.T("Xóa"), null, _ => RemoveAsync(), this);
    }

    /// <summary>Reads the disk again for received files deleted or moved since ("Đã xóa file").</summary>
    internal void CheckReceivedFiles()
    {
        foreach (OfferItemViewModel offer in Offers)
        {
            offer.CheckFiles();
        }
    }

    [RelayCommand]
    private void ShowReceived() => FilesShowSent = false;

    [RelayCommand]
    private void ShowSentFiles()
    {
        FilesShowSent = true;
        SelectedTab = 1;
    }

    /// <summary>
    /// The microphone permission, asked the first time 🎤 is held. Its dialog takes the finger away, so that time records
    /// nothing: once granted, holding again does.
    /// </summary>
    private async Task<bool> AllowRecordingAsync()
    {
        if (MainActivity.Current is not { } activity)
        {
            return false;
        }

        if (activity.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == global::Android.Content.PM.Permission.Granted)
        {
            return true;
        }

        Chat.Notice = await PlaybackCapture.RequestMicrophoneAsync(activity)
            ? L.T("Đã có quyền micrô: giữ nút micrô để ghi.")
            : L.T("Tin nhắn thoại cần quyền micrô. Nếu đã từ chối, bật lại trong Cài đặt → Ứng dụng → Shorekeeper → Quyền.");
        return false;
    }

    /// <summary>"Nhắn tin" on a card, or a message notification.</summary>
    internal void OpenChat(DeviceId peer)
    {
        Chat.Open(peer);
        SelectedTab = 2;
    }

    /// <summary>"Nhắn tin" on a group, or a group message notification.</summary>
    internal void OpenGroupChat(string groupId)
    {
        Chat.OpenGroup(groupId);
        SelectedTab = 2;
    }

    partial void OnIsInForegroundChanged(bool value) => Chat.IsShown = value && IsChatTab;

    partial void OnFilesShowSentChanged(bool value)
    {
        if (!value && IsFilesTab)
        {
            CheckReceivedFiles();
        }
    }

    partial void OnSelectedTabChanged(int value)
    {
        Chat.IsShown = IsInForeground && value == 2;
        if (value == 1 && !FilesShowSent)
        {
            CheckReceivedFiles();
        }
        else if (value == 5)
        {
            Settings.Stats.RefreshCommand.Execute(null);
        }
    }

    internal void Download(OfferItemViewModel offer) => inbox.Download(offer.Key);

    internal Task DeclineAsync(OfferItemViewModel offer) => inbox.DeclineAsync(offer.Key);

    /// <summary>"/storage/emulated/0/" before "Download/Shorekeeper": the shared storage is all the user sees of it.</summary>
    [GeneratedRegex(@"^/storage/emulated/\d+/")]
    private static partial Regex SharedStoragePrefix();

    /// <summary>"482913" → "482 913", as the desktop shows it.</summary>
    internal static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private void RefreshPeers()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        List<PeerItemViewModel> items =
        [
            .. trust.GetAll(TrustLevel.Trusted).Select(record => new PeerItemViewModel(
                record.DeviceId, record.ShownName, record.HostName, Detail(live.GetValueOrDefault(record.DeviceId), record.HostName), true,
                live.GetValueOrDefault(record.DeviceId), record.AutoAcceptMaxBytes is not null, this)
            {
                IsPhone = live.TryGetValue(record.DeviceId, out PeerInfo? peer)
                    ? peer.Os.Equals("android", StringComparison.OrdinalIgnoreCase)
                    : record.IsPhone,
            }),
            .. live.Values
                .Where(peer => trust.GetLevel(peer.DeviceId) == TrustLevel.Unknown)
                .Select(peer => new PeerItemViewModel(
                    peer.DeviceId, peer.DisplayName, peer.HostName, Detail(peer, peer.HostName), false, peer, false, this)),
        ];
        items.Sort((x, y) => x.IsTrusted != y.IsTrusted ? (x.IsTrusted ? -1 : 1) : CultureInfo.CurrentCulture.CompareInfo.Compare(x.Name, y.Name, CompareOptions.IgnoreCase));

        Peers.Clear();
        foreach (PeerItemViewModel item in items)
        {
            Peers.Add(item);
        }

        OnPropertyChanged(nameof(HasNoPeers));
    }

    private void RefreshOffers()
    {
        IReadOnlyList<ReceivedOffer> current = inbox.GetOffers();
        // Text alone lives in the conversation (docs/17-ui-cleanup.md §2).
        Sync(Offers, current.Where(o => o.Files.Count > 0).OrderByDescending(o => o.ReceivedAt), o => new OfferKey(o.SenderId, o.OfferId), item => item.Key,
            o => new OfferItemViewModel(o, this), (item, o) => item.Update(o, NameOf(o.SenderId)));

        progressTimer.IsEnabled = current.Any(o => o.State == InboxState.Downloading)
            || offers.GetOffers().Any(o => o.Recipients.Any(r => r.State == RecipientState.Downloading));
        OnPropertyChanged(nameof(HasNoOffers));
        OfferSelection.Changed();
    }

    private void RefreshSent()
    {
        IReadOnlyList<SentOffer> current = offers.GetOffers();
        Sync(Sent, current.Where(o => o.Paths.Count > 0).OrderByDescending(o => o.CreatedAt), o => o.OfferId, item => item.OfferId,
            o => new SentItemViewModel(o, this), (item, o) => item.Update(o));

        progressTimer.IsEnabled |= current.Any(o => o.Recipients.Any(r => r.State == RecipientState.Downloading));
        OnPropertyChanged(nameof(HasNoSent));
        SentSelection.Changed();
    }

    /// <summary>
    /// Makes <paramref name="items"/> match <paramref name="data"/> in order, updating the items it already has: rebuilding
    /// the list (as on every progress tick) made scrolling stutter on phones.
    /// </summary>
    private static void Sync<TItem, TData, TKey>(
        ObservableCollection<TItem> items, IEnumerable<TData> data, Func<TData, TKey> keyOf, Func<TItem, TKey> itemKey,
        Func<TData, TItem> create, Action<TItem, TData> update)
        where TKey : notnull
    {
        int index = 0;
        foreach (TData value in data)
        {
            TKey key = keyOf(value);
            int existing = -1;
            for (int i = index; i < items.Count; i++)
            {
                if (EqualityComparer<TKey>.Default.Equals(itemKey(items[i]), key))
                {
                    existing = i;
                    break;
                }
            }

            if (existing < 0)
            {
                TItem item = create(value);
                update(item, value);
                items.Insert(index, item);
            }
            else
            {
                if (existing != index)
                {
                    items.Move(existing, index);
                }

                update(items[index], value);
            }

            index++;
        }

        while (items.Count > index)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    private static string Detail(PeerInfo? live, string host) => live is null ? host : $"{live.HostName} · {live.Address}";
}
