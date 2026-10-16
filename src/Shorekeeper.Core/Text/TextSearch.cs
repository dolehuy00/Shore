using System.Globalization;
using System.Text;

namespace Shorekeeper.Core.Text;

/// <summary>Search as people type it: no case, no Vietnamese accents ("thang 9" finds "Báo cáo tháng 9").</summary>
public static class TextSearch
{
    /// <summary>Every word of <paramref name="query"/> appears in one of <paramref name="fields"/>. An empty query matches nothing.</summary>
    public static bool Matches(string query, params IEnumerable<string?> fields)
    {
        string[] words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        string text = string.Join(' ', fields.OfType<string>().Select(Fold));
        return words.All(word => text.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>Lower case without accents; "đ" becomes "d" (it is a letter of its own, not a d with a mark).</summary>
    public static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
            }
        }

        return folded.ToString();
    }
}
