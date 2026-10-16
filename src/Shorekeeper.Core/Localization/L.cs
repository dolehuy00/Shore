using System.Globalization;
using System.Text.Json;

namespace Shorekeeper.Core.Localization;

/// <summary>
/// User-facing text (docs/10-ux.md §1: Vietnamese first, English otherwise). The Vietnamese sentence is its own key,
/// so code stays readable: <c>L.T("Đã tải xong")</c>, <c>L.F("Đã kết nối với {0}.", name)</c>. English comes from the
/// embedded <c>en.json</c>; a sentence it lacks is shown in Vietnamese.
/// </summary>
public static class L
{
    /// <summary>Overrides the system language, e.g. <c>SHOREKEEPER_LANGUAGE=en</c> to try the English text.</summary>
    public const string OverrideVariable = "SHOREKEEPER_LANGUAGE";

    public const string Vietnamese = "vi";

    public const string English = "en";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishText = new(LoadEnglish);

    /// <summary>True when the text is shown in Vietnamese: the system language is Vietnamese (or the override says so).</summary>
    public static bool IsVietnamese { get; set; } = DetectVietnamese();

    /// <summary>Numbers and dates in the language of the text ("1,5 GB" or "1.5 GB").</summary>
    public static CultureInfo Culture => IsVietnamese ? CultureInfo.GetCultureInfo("vi-VN") : CultureInfo.GetCultureInfo("en-US");

    /// <summary>Translates a Vietnamese sentence.</summary>
    public static string T(string vietnamese) =>
        IsVietnamese || !EnglishText.Value.TryGetValue(vietnamese, out string? english) ? vietnamese : english;

    /// <summary>Translates a Vietnamese format ("{0}" placeholders), then fills it in.</summary>
    public static string F(string vietnameseFormat, params object?[] args) => string.Format(Culture, T(vietnameseFormat), args);

    /// <summary>
    /// Applies the language chosen in Settings (<see cref="Vietnamese"/> or <see cref="English"/>) before any text is shown;
    /// null keeps the system language. <see cref="OverrideVariable"/> still wins.
    /// </summary>
    public static void Use(string? language)
    {
        if (language is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OverrideVariable)))
        {
            IsVietnamese = IsVietnameseCode(language);
        }
    }

    /// <summary>The embedded English catalog, for tests.</summary>
    public static IReadOnlyDictionary<string, string> EnglishCatalog => EnglishText.Value;

    private static bool DetectVietnamese()
    {
        string? forced = Environment.GetEnvironmentVariable(OverrideVariable);
        return IsVietnameseCode(string.IsNullOrWhiteSpace(forced) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : forced.Trim());
    }

    private static bool IsVietnameseCode(string language) => language.StartsWith(Vietnamese, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> LoadEnglish()
    {
        using Stream? stream = typeof(L).Assembly.GetManifestResourceStream("Shorekeeper.Localization.en.json");
        if (stream is null)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(stream);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            catalog[entry.Name] = entry.Value.GetString() ?? entry.Name;
        }

        return catalog;
    }
}
