using Shorekeeper.Core.Localization;

namespace Shorekeeper.Desktop.Localization;

/// <summary>An entry of "Ngôn ngữ" in Settings (desktop and Android).</summary>
/// <param name="Code">"vi", "en", or null for the system language.</param>
public sealed record LanguageChoice(string? Code, string Label)
{
    /// <summary>Each language named in itself, so it can be found whatever the current one is.</summary>
    public static IReadOnlyList<LanguageChoice> All { get; } =
    [
        new(null, L.T("Theo hệ điều hành")), new(L.Vietnamese, "Tiếng Việt"), new(L.English, "English"),
    ];

    public static LanguageChoice Of(string? code) => All.FirstOrDefault(c => c.Code == code) ?? All[0];
}
