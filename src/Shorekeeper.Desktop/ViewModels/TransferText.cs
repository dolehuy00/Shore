using System.Text.RegularExpressions;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>What an offer mostly holds, for its icon.</summary>
public enum TransferKind
{
    Files,
    Images,
    Folder,
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
