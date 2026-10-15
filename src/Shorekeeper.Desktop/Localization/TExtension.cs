using Shorekeeper.Core.Localization;

namespace Shorekeeper.Desktop.Localization;

/// <summary>Translated text in AXAML: <c>Content="{l:T 'Gửi file…'}"</c> (see <see cref="L"/>).</summary>
public sealed class TExtension(string text)
{
    public string ProvideValue(IServiceProvider serviceProvider) => L.T(text);
}
