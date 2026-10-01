using System.Globalization;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>Wording shared by the inbox, the sent list and the incoming-offer popup.</summary>
public static class TransferText
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Vietnamese, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(Vietnamese, "{0:0.0} MB", bytes / (double)(1L << 20)),
        >= 1L << 10 => string.Format(Vietnamese, "{0:0} KB", bytes / (double)(1L << 10)),
        _ => string.Format(Vietnamese, "{0} B", bytes),
    };

    /// <summary>"app.zip", "project (thư mục)", "app.zip + 2 mục" from the top-level entries of an offer.</summary>
    public static string Summary(IEnumerable<string> relativePaths)
    {
        string[] topLevel = [.. relativePaths.Where(IsTopLevel)];
        if (topLevel.Length == 0)
        {
            return "(trống)";
        }

        string first = topLevel[0].EndsWith('/') ? $"{topLevel[0][..^1]} (thư mục)" : topLevel[0];
        return topLevel.Length == 1 ? first : $"{first} + {topLevel.Length - 1} mục";
    }

    public static string Remaining(TimeSpan left) => left switch
    {
        { TotalSeconds: <= 0 } => "hết hạn",
        { TotalHours: >= 1 } => $"còn {(int)left.TotalHours} giờ",
        { TotalMinutes: >= 1 } => $"còn {(int)left.TotalMinutes} phút",
        _ => "sắp hết hạn",
    };

    private static bool IsTopLevel(string path)
    {
        int slash = path.IndexOf('/');
        return slash < 0 || slash == path.Length - 1;
    }
}
