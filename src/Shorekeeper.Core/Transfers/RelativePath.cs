using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Shorekeeper.Core.Transfers;

/// <summary>
/// Validates and cleans a relative path received from a peer before it touches the disk
/// (docs/04-identity-security.md §5). Paths use '/' and end with '/' for directories.
/// Anything that could escape the download folder is rejected; awkward names are made safe.
/// </summary>
public static class RelativePath
{
    public const int MaxLength = 1024;
    public const int MaxSegmentLength = 255;
    public const int MaxDepth = 64;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"\\|?*");

    /// <param name="segments">Cleaned path segments; join them under the destination folder.</param>
    /// <param name="isDirectory">True when the path ends with '/'.</param>
    public static bool TrySanitize(string? path, [NotNullWhen(true)] out string[]? segments, out bool isDirectory)
    {
        segments = null;
        isDirectory = false;
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || path[0] == '/')
        {
            return false;
        }

        isDirectory = path.EndsWith('/');
        string[] parts = (isDirectory ? path[..^1] : path).Split('/');
        if (parts.Length > MaxDepth)
        {
            return false;
        }

        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (TryCleanSegment(parts[i]) is not { } clean)
            {
                return false;
            }

            result[i] = clean;
        }

        segments = result;
        return true;
    }

    private static string? TryCleanSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > MaxSegmentLength || segment is "." or "..")
        {
            return null;
        }

        if (segment.AsSpan().ContainsAny(Forbidden) || segment.Any(c => c < 0x20))
        {
            return null;
        }

        // Bidirectional/format characters (e.g. RIGHT-TO-LEFT OVERRIDE) can disguise an extension: "invoice[RLO]fdp.exe" displays as "invoiceexe.pdf".
        string clean = new([.. segment.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format)]);

        // Windows silently drops trailing dots and spaces, which could merge two different names.
        clean = clean.TrimEnd('.', ' ');
        if (clean.Length == 0)
        {
            return null;
        }

        string stem = clean.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + clean : clean;
    }
}
