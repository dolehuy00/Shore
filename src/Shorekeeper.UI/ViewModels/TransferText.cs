using System.Text.RegularExpressions;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Text;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.UI.ViewModels;

/// <summary>What an offer mostly holds, for its icon.</summary>
public enum TransferKind
{
    Files,
    Images,
    Folder,
}

/// <summary>"Tất cả · Ảnh · File · Thư mục" on the Files page (docs/17-ui-cleanup.md §4).</summary>
public enum FileFilter
{
    All,
    Images,
    Files,
    Folders,
}

/// <summary>A row of the Files page, which the filter, the search box and the day headings apply to.</summary>
public interface IFileRow
{
    DateTimeOffset At { get; }

    TransferKind Kind { get; }

    /// <summary>Who and which files, for the search box.</summary>
    IEnumerable<string> SearchFields { get; }

    bool IsShown { get; set; }

    /// <summary>"Hôm nay", "Hôm qua"…: set on the first shown row of each, null on the others.</summary>
    string? DayText { get; set; }
}

/// <summary>Wording shared by the inbox, the sent list and the incoming-offer popup.</summary>
public static partial class TransferText
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(L.Culture, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(L.Culture, "{0:0.0} MB", bytes / (double)(1L << 20)),
        >= 1L << 10 => string.Format(L.Culture, "{0:0} KB", bytes / (double)(1L << 10)),
        _ => string.Format(L.Culture, "{0} B", bytes),
    };

    /// <summary>"app.zip", "project (thư mục)", "app.zip + 2 mục" from the top-level entries of an offer.</summary>
    public static string Summary(IEnumerable<string> relativePaths)
    {
        string[] topLevel = [.. relativePaths.Where(IsTopLevel)];
        if (topLevel.Length == 0)
        {
            return L.T("(trống)");
        }

        string first = topLevel[0].EndsWith('/') ? L.F("{0} (thư mục)", topLevel[0][..^1]) : topLevel[0];
        return topLevel.Length == 1 ? first : L.F("{0} + {1} mục", first, topLevel.Length - 1);
    }

    /// <summary>"app.zip", "project (thư mục · 5 file)": the files and folders at the top of an offer, in its order.</summary>
    public static IReadOnlyList<string> TopLevel(IEnumerable<string> relativePaths)
    {
        List<string> entries = [];
        Dictionary<string, int> filesIn = new(StringComparer.Ordinal);
        foreach (string path in relativePaths)
        {
            int slash = path.IndexOf('/');
            string entry = slash < 0 ? path : path[..(slash + 1)];
            if (filesIn.TryAdd(entry, 0))
            {
                entries.Add(entry);
            }

            if (slash >= 0 && !path.EndsWith('/'))
            {
                filesIn[entry]++;
            }
        }

        return [.. entries.Select(e => e.EndsWith('/') ? L.F("{0} (thư mục · {1})", e[..^1], FileCount(filesIn[e])) : e)];
    }

    /// <summary>The text when it is a single web address (http or https), so it can be opened; null otherwise.</summary>
    public static Uri? LinkOf(string? text) =>
        text?.Trim() is { Length: > 0 } trimmed && !trimmed.Any(char.IsWhiteSpace)
        && Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>The web addresses in a message, at most three, each once.</summary>
    public static IReadOnlyList<string> LinksIn(string text) =>
        [.. WebAddress().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']'))
            .Where(link => LinkOf(link) is not null)
            .Distinct()
            .Take(3)];

    /// <summary>The first line of a text, shortened for a title or a notification.</summary>
    public static string Preview(string text, int maxLength = 80)
    {
        string line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= maxLength ? line : line[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The words around where <paramref name="query"/> first matches (accents and case aside), on one line, shortened
    /// with "…" at either end; the start of the text when nothing matches.
    /// </summary>
    public static string Snippet(string text, string query, int maxLength = 80)
    {
        string line = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length <= maxLength)
        {
            return line;
        }

        // Folded letter by letter, so a position in the folded line is the same in the line.
        string folded = string.Concat(line.Select(c => TextSearch.Fold(c.ToString()) is { Length: 1 } f ? f : "\0"));
        int at = TextSearch.Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => folded.IndexOf(word, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        // A little before the match, from the start of a word.
        int start = Math.Max(0, at - 20);
        if (start > 0 && line.IndexOf(' ', start) is var space and >= 0 && space < at)
        {
            start = space + 1;
        }

        start = Math.Min(start, Math.Max(0, line.Length - maxLength + 1));
        int room = maxLength - (start > 0 ? 1 : 0);
        bool cutEnd = line.Length - start > room;
        string middle = line.Substring(start, cutEnd ? room - 1 : line.Length - start);
        // Cut at the end of a word, unless that loses much.
        if (cutEnd && middle.LastIndexOf(' ') is var wordEnd && wordEnd > middle.Length * 2 / 3)
        {
            middle = middle[..wordEnd];
        }

        return (start > 0 ? "…" : "") + middle.Trim() + (cutEnd ? "…" : "");
    }

    public static string Remaining(TimeSpan left) => left switch
    {
        { TotalSeconds: <= 0 } => L.T("hết hạn"),
        { TotalHours: >= 1 } => L.F("còn {0} giờ", (int)left.TotalHours),
        { TotalMinutes: >= 1 } => L.F("còn {0} phút", (int)left.TotalMinutes),
        _ => L.T("sắp hết hạn"),
    };

    public static string FileCount(int count) => count == 1 ? L.T("1 file") : L.F("{0} file", count);

    /// <summary>"vừa xong", "5 phút trước", "3 giờ trước", "hôm qua", then the date.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now) => (now - at) switch
    {
        { TotalMinutes: < 1 } => L.T("vừa xong"),
        { TotalHours: < 1 } span => L.F("{0} phút trước", (int)span.TotalMinutes),
        { TotalDays: < 1 } span => L.F("{0} giờ trước", (int)span.TotalHours),
        { TotalDays: < 2 } => L.T("hôm qua"),
        _ => L.F("{0:dd/MM}", at.ToLocalTime()),
    };

    /// <summary>"Hôm nay", "Hôm qua", "7 ngày qua", "Trước đó", by the local day.</summary>
    public static string DayGroup(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime day = at.ToLocalTime().Date;
        DateTime today = now.ToLocalTime().Date;
        return day >= today ? L.T("Hôm nay")
            : day == today.AddDays(-1) ? L.T("Hôm qua")
            : day > today.AddDays(-7) ? L.T("7 ngày qua")
            : L.T("Trước đó");
    }

    /// <summary>
    /// Shows the rows the filter and the search keep (all words, no accents, as Ctrl+K), newest first as given, with a day
    /// heading above the first of each day group.
    /// </summary>
    public static void ApplyView(IEnumerable<IFileRow> rows, FileFilter filter, string query, DateTimeOffset now)
    {
        string? last = null;
        foreach (IFileRow row in rows)
        {
            row.IsShown = filter switch
            {
                FileFilter.Images => row.Kind == TransferKind.Images,
                FileFilter.Files => row.Kind == TransferKind.Files,
                FileFilter.Folders => row.Kind == TransferKind.Folder,
                _ => true,
            } && (string.IsNullOrWhiteSpace(query) || TextSearch.Matches(query, row.SearchFields));
            if (!row.IsShown)
            {
                row.DayText = null;
                continue;
            }

            string group = DayGroup(row.At, now);
            row.DayText = group == last ? null : group;
            last = group;
        }
    }

    /// <summary>A folder at the top, only pictures, or anything else.</summary>
    public static TransferKind KindOf(IReadOnlyCollection<string> relativePaths) =>
        relativePaths.Any(p => p.Contains('/', StringComparison.Ordinal)) ? TransferKind.Folder
        : relativePaths.Count > 0 && relativePaths.All(IsImage) ? TransferKind.Images
        : TransferKind.Files;

    /// <summary>Like a browser's downloads: how many of the received files are still where they were saved. Reads the disk.</summary>
    public static (int Present, int Received) OnDisk(IEnumerable<InboxFile> files)
    {
        string[] received = [.. files.Where(f => !f.IsDirectory && f.State == InboxFileState.Completed && f.FinalPath is not null).Select(f => f.FinalPath!)];
        return (received.Count(File.Exists), received.Length);
    }

    /// <summary>"Đã xóa file" when none of the received files are there any more, "Còn 3/5 file" when some are gone.</summary>
    public static string? Gone((int Present, int Received) files) =>
        files.Present == files.Received ? null
        : files.Present == 0 ? L.T("Đã xóa file")
        : L.F("Còn {0}/{1} file", files.Present, files.Received);

    /// <summary>A picture the app can show as a thumbnail.</summary>
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    private static bool IsTopLevel(string path)
    {
        int slash = path.IndexOf('/');
        return slash < 0 || slash == path.Length - 1;
    }
}
