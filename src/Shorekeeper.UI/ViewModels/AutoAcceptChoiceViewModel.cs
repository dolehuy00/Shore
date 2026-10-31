using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// "Tự nhận file" for one contact: off, up to the limit in Settings, or up to its own limit in GB.
/// </summary>
/// <param name="current">The contact's <see cref="PeerRecord.AutoAcceptMaxBytes"/>.</param>
/// <param name="save">Stores the choice (null = off, <see cref="TrustStore.FollowSettingsLimit"/>, or bytes).</param>
public sealed partial class AutoAcceptChoiceViewModel(string name, long? current, double generalLimitGb, Func<long?, Task> save, Action close)
    : ObservableObject
{
    public string Title { get; } = L.F("Tự nhận file từ {0}", name);

    public string FollowText { get; } = L.F("Theo cài đặt chung (tối đa {0})", GbText(generalLimitGb));

    [ObservableProperty]
    public partial bool IsOff { get; set; } = current is null;

    [ObservableProperty]
    public partial bool IsFollow { get; set; } = current == TrustStore.FollowSettingsLimit;

    [ObservableProperty]
    public partial bool IsCustom { get; set; } = current is > 0;

    /// <summary>The contact's own limit in GB.</summary>
    [ObservableProperty]
    public partial decimal? CustomGb { get; set; } = current is > 0 and var bytes ? Math.Round((decimal)bytes / (1L << 30), 2) : (decimal)generalLimitGb;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>"2 GB", "0,5 GB".</summary>
    public static string GbText(double gb) => string.Format(L.Culture, "{0:0.##} GB", gb);

    /// <summary>Typing a limit picks "Riêng".</summary>
    partial void OnCustomGbChanged(decimal? value) => IsCustom = true;

    // One choice at a time, whichever way it was picked.
    partial void OnIsOffChanged(bool value)
    {
        if (value)
        {
            IsFollow = IsCustom = false;
        }
    }

    partial void OnIsFollowChanged(bool value)
    {
        if (value)
        {
            IsOff = IsCustom = false;
        }
    }

    partial void OnIsCustomChanged(bool value)
    {
        if (value)
        {
            IsOff = IsFollow = false;
        }
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        long? maxBytes;
        if (IsOff)
        {
            maxBytes = null;
        }
        else if (IsFollow)
        {
            maxBytes = TrustStore.FollowSettingsLimit;
        }
        else if (CustomGb is { } gb && gb > 0 && gb <= (decimal)EffectiveSettings.MaxAutoAcceptLimitGb)
        {
            maxBytes = (long)(gb * (1L << 30));
        }
        else
        {
            Error = L.F("Nhập số GB lớn hơn 0 và không quá {0}.", EffectiveSettings.MaxAutoAcceptLimitGb.ToString("N0", L.Culture));
            return;
        }

        close();
        await save(maxBytes);
    }

    [RelayCommand]
    private void Cancel() => close();
}
