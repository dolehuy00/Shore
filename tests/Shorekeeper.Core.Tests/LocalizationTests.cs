using System.Net;
using System.Text.RegularExpressions;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Core.Tests;

public partial class LocalizationTests
{
    [Fact]
    public void English_keeps_the_placeholders_of_each_sentence()
    {
        Assert.NotEmpty(L.EnglishCatalog);
        foreach ((string vietnamese, string english) in L.EnglishCatalog)
        {
            Assert.True(Placeholders(vietnamese).SetEquals(Placeholders(english)), $"{vietnamese} -> {english}");
        }
    }

    [Fact]
    public void Every_sentence_in_the_apps_has_an_english_translation()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            IEnumerable<string> keys = Path.GetExtension(file) switch
            {
                ".cs" => CodeKey().Matches(File.ReadAllText(file)).Select(m => Regex.Unescape(m.Groups[1].Value)),
                ".axaml" => MarkupKey().Matches(File.ReadAllText(file)).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)),
                _ => [],
            };
            missing.AddRange(keys.Where(k => !L.EnglishCatalog.ContainsKey(k)).Select(k => $"{Path.GetFileName(file)}: {k}"));
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Text_follows_the_language()
    {
        bool original = L.IsVietnamese;
        try
        {
            L.IsVietnamese = true;
            Assert.Equal("Đã lưu.", L.T("Đã lưu."));
            Assert.Equal("Quá nhiều file: tối đa 1.024 file mỗi lần gửi.", L.F("Quá nhiều file: tối đa {0:N0} file mỗi lần gửi.", 1024));

            L.IsVietnamese = false;
            Assert.Equal("Saved.", L.T("Đã lưu."));
            Assert.Equal("Too many files: at most 1,024 per send.", L.F("Quá nhiều file: tối đa {0:N0} file mỗi lần gửi.", 1024));
            Assert.Equal("Chưa dịch", L.T("Chưa dịch"));
        }
        finally
        {
            L.IsVietnamese = original;
        }
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Shorekeeper.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Shorekeeper.sln not found above the test output.");
    }

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"L\.[TF]\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:T '([^']*)'\}")]
    private static partial Regex MarkupKey();
}
