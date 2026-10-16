using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// "Xem cả 3 mục": every file and folder at the top of an offer, as the title ("app.zip + 2 mục") names only the first.
/// </summary>
/// <param name="expandedChanged">For lists rebuilt on every change, to keep the list open across rebuilds.</param>
public sealed partial class OfferContents(IReadOnlyCollection<string> relativePaths, bool expanded = false, Action<bool>? expandedChanged = null)
    : ObservableObject
{
    public IReadOnlyList<string> Items { get; private set; } = TransferText.TopLevel(relativePaths);

    public bool CanExpand => Items.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    public partial bool IsExpanded { get; set; } = expanded;

    public string ToggleText => IsExpanded ? L.T("Ẩn danh sách") : L.F("Xem cả {0} mục", Items.Count);

    public void Update(IReadOnlyCollection<string> paths)
    {
        IReadOnlyList<string> items = TransferText.TopLevel(paths);
        if (!items.SequenceEqual(Items))
        {
            Items = items;
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(CanExpand));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        expandedChanged?.Invoke(IsExpanded);
    }
}
