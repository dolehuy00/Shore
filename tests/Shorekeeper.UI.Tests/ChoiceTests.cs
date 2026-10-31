using Shorekeeper.Core.Localization;
using Shorekeeper.UI.Localization;
using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.UI.Tests;

public class ChoiceTests
{
    [Fact]
    public void A_saved_theme_or_language_is_found_and_anything_else_follows_the_system()
    {
        Assert.Equal(ThemeChoice.Dark, ThemeChoice.Of("dark").Code);
        Assert.Equal(ThemeChoice.Light, ThemeChoice.Of("light").Code);
        Assert.Null(ThemeChoice.Of(null).Code);
        Assert.Null(ThemeChoice.Of("purple").Code);

        Assert.Equal(L.English, LanguageChoice.Of("en").Code);
        Assert.Null(LanguageChoice.Of("fr").Code);
    }

    [Fact]
    public void Each_choice_is_named_in_Settings()
    {
        Assert.Equal(["Theo hệ điều hành", "Sáng", "Tối"], ThemeChoice.All.Select(c => c.Label));
        Assert.Equal(["Theo hệ điều hành", "Tiếng Việt", "English"], LanguageChoice.All.Select(c => c.Label));
    }

    [Fact]
    public void Limits_in_GB_drop_needless_decimals() =>
        Assert.Equal(["2 GB", "0,5 GB", "1,25 GB"], ((double[])[2, 0.5, 1.25]).Select(AutoAcceptChoiceViewModel.GbText));
}
