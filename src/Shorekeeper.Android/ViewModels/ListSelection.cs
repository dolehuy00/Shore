using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.ViewModels;

/// <summary>A card that can be checked while its list is in "Chọn".</summary>
public interface ISelectableItem
{
    bool IsSelected { get; set; }

    ListSelection Selection { get; }
}

/// <summary>"Chọn" on the Hộp nhận or Đã gửi tab (docs/10-ux.md §4): check boxes on the cards and how many are checked.</summary>
public sealed partial class ListSelection(Func<IEnumerable<ISelectableItem>> items) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    public int Count => items().Count(i => i.IsSelected);

    public bool HasAny => Count > 0;

    public string Text => L.F("Đã chọn {0}", Count);

    /// <summary>"Chọn tất cả": checked when all are, empty when none, in between otherwise.</summary>
    public bool? All
    {
        get => Count == 0 ? false : Count == items().Count() ? true : null;
        set
        {
            bool select = Count < items().Count();
            foreach (ISelectableItem item in items())
            {
                item.IsSelected = select;
            }
        }
    }

    public IReadOnlyList<T> Selected<T>() => [.. items().Where(i => i.IsSelected).OfType<T>()];

    /// <summary>A check box changed, or the list did.</summary>
    public void Changed()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(All));
    }

    /// <summary>Long press on a card, or "Chọn nhiều" in its ⋮ menu: choosing starts with that card checked.</summary>
    public void StartWith(ISelectableItem item)
    {
        IsOn = true;
        item.IsSelected = true;
    }

    [RelayCommand]
    public void Stop()
    {
        IsOn = false;
        foreach (ISelectableItem item in items())
        {
            item.IsSelected = false;
        }
    }
}
