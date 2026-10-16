using Shorekeeper.Core.Localization;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>What an offer mostly holds, for its icon.</summary>
public enum TransferKind
{
    Files,
    Images,
    Folder,
}

/// <summary>Wording shared by the inbox, the sent list and the incoming-offer popup.</summary>
public static class TransferText
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

    /// <summary>A picture the app can show as a thumbnail.</summary>
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsTopLevel(string path)
    {
        int slash = path.IndexOf('/');
        return slash < 0 || slash == path.Length - 1;
    }
}
