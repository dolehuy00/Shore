using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Text;
using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.Desktop.ViewModels;

public enum SearchKind
{
    Person,
    Group,
    Received,
    Sent,

    /// <summary>A message, by its words (docs/18-chat-extras.md §3).</summary>
    Message,
}

/// <param name="Item">The row's view model, revealed on its page when the result is chosen.</param>
/// <param name="ActionText">The quick action next to the result ("Gửi file…", "Mở"), if any.</param>
public sealed record SearchResult(SearchKind Kind, string Title, string Subtitle, object Item, string? ActionText = null, ICommand? Action = null)
{
    public bool HasAction => Action is not null;
}

/// <summary>
/// Ctrl+K in the header (docs/10-ux.md §1): people, groups, received and sent files in one list, then messages found by
/// their words, which come from the database a moment later; choosing a result opens its page and points at it.
/// </summary>
public sealed partial class SearchViewModel(
    NeighborhoodViewModel people, GroupsViewModel groups, InboxViewModel inbox, SentViewModel sent, ChatViewModel chat,
    Action<SearchResult> reveal) : ObservableObject
{
    private const int PerKind = 5;

    /// <summary>Messages are many and what people look for most: more of them.</summary>
    private const int Messages = 8;

    /// <summary>Counts the words typed: only the newest search adds its messages.</summary>
    private int version;

    public ObservableCollection<SearchResult> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial SearchResult? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>↑/↓ in the search box.</summary>
    public void Move(int delta)
    {
        if (Results.Count > 0)
        {
            int index = Selected is null ? -1 : Results.IndexOf(Selected);
            Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
        }
    }

    public void Close()
    {
        Query = "";
        IsOpen = false;
    }

    [RelayCommand]
    private void Choose(SearchResult? result)
    {
        if ((result ?? Selected) is { } chosen)
        {
            Close();
            reveal(chosen);
        }
    }

    [RelayCommand]
    private void RunAction(SearchResult result)
    {
        Close();
        result.Action?.Execute(null);
    }

    private void Refresh()
    {
        string query = Query;
        Results.Clear();
        foreach (SearchResult result in Find(query))
        {
            Results.Add(result);
        }

        Selected = Results.FirstOrDefault();
        IsOpen = query.Trim().Length > 0;
        // "Không tìm thấy" waits for the messages.
        HasNoResults = false;
        _ = AddMessagesAsync(query, ++version);
    }

    private async Task AddMessagesAsync(string query, int at)
    {
        if (!IsOpen)
        {
            return;
        }

        // A short pause, so each letter typed does not read the messages again.
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        if (at != version)
        {
            return;
        }

        IReadOnlyList<ChatSearchHit> hits = await chat.FindAsync(query, CancellationToken.None);
        if (at != version || !IsOpen)
        {
            return;
        }

        foreach (ChatSearchHit hit in hits.Take(Messages))
        {
            Results.Add(new SearchResult(SearchKind.Message, hit.Conversation, $"{hit.Snippet} · {hit.TimeText}", hit));
        }

        Selected ??= Results.FirstOrDefault();
        HasNoResults = Results.Count == 0;
    }

    private IEnumerable<SearchResult> Find(string query)
    {
        IEnumerable<SearchResult> found =
        [
            .. people.Everyone
                .Where(p => TextSearch.Matches(query, p.DisplayName, p.HostName, p.Subtitle, p.Department))
                .Take(PerKind)
                .Select(p => p.CanSend ? new SearchResult(SearchKind.Person, p.DisplayName, p.Subtitle, p, L.T("Gửi file…"), p.SendFilesCommand)
                    : p.CanConnect ? new SearchResult(SearchKind.Person, p.DisplayName, p.Subtitle, p, L.T("Kết nối"), p.ConnectCommand)
                    : new SearchResult(SearchKind.Person, p.DisplayName, p.Subtitle, p)),
            .. groups.MyGroups
                .Where(g => TextSearch.Matches(query, g.Name))
                .Take(PerKind)
                .Select(g => g.CanDrop ? new SearchResult(SearchKind.Group, g.Name, g.Subtitle, g, L.T("Gửi file cho nhóm"), g.SendFilesCommand)
                    : new SearchResult(SearchKind.Group, g.Name, g.Subtitle, g)),
            .. groups.NetworkGroups
                .Where(g => TextSearch.Matches(query, g.Name))
                .Take(PerKind)
                .Select(g => new SearchResult(SearchKind.Group, g.Name, g.Detail, g, L.T("Xin vào"), g.JoinCommand)),
            .. inbox.Items
                .Where(i => TextSearch.Matches(query, [i.SenderName, .. i.FileNames]))
                .Take(PerKind)
                .Select(i => new SearchResult(SearchKind.Received, i.Title, L.F("Từ {0} · {1}", i.SenderName, i.Details), i,
                    i.CanOpen ? L.T("Mở") : i.CanDownload ? i.DownloadText : null,
                    i.CanOpen ? i.OpenCommand : i.CanDownload ? i.DownloadCommand : null)),
            .. sent.Items
                .Where(s => TextSearch.Matches(query, s.Paths))
                .Take(PerKind)
                .Select(s => new SearchResult(SearchKind.Sent, s.Title, s.Details, s)),
        ];
        return found;
    }
}
