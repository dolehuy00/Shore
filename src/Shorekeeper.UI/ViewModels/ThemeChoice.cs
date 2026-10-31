using Avalonia.Styling;
using Shorekeeper.Core.Localization;
using Application = Avalonia.Application;

namespace Shorekeeper.UI.ViewModels;

/// <summary>An entry of "Giao diện" in Settings (desktop and Android).</summary>
/// <param name="Code">"light", "dark", or null for the system's.</param>
public sealed record ThemeChoice(string? Code, string Label)
{
    public const string Light = "light";
    public const string Dark = "dark";

    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new(null, L.T("Theo hệ điều hành")), new(Light, L.T("Sáng")), new(Dark, L.T("Tối")),
    ];

    public static ThemeChoice Of(string? code) => All.FirstOrDefault(c => c.Code == code) ?? All[0];

    /// <summary>Applies at once to every window, title bars and the phone's status bar included.</summary>
    public static void Apply(string? code)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = code switch
            {
                Light => ThemeVariant.Light,
                Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
