using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Desktop.Views;

namespace Shorekeeper.Desktop.ViewModels;

/// <param name="groupId">Null for contacts; for fellow members, the group the offer goes through.</param>
public sealed partial class QuickSendPerson(DeviceId deviceId, string name, string detail, string? groupId) : ObservableObject
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public string? GroupId { get; } = groupId;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// "Gửi nhanh" (docs/10-ux.md §10–11): files from Explorer, a drop or a picker, then the people to send them to.
/// People sent to lately come first.
/// </summary>
public sealed partial class QuickSendViewModel : DialogViewModel
{
    private readonly IReadOnlyList<QuickSendPerson> everyone;

    public QuickSendViewModel(IReadOnlyList<QuickSendPerson> people, IReadOnlyList<string> paths)
    {
        everyone = people;
        foreach (QuickSendPerson person in everyone)
        {
            person.PropertyChanged += (_, _) => OnSelectionChanged();
        }

        Paths.CollectionChanged += (_, _) => OnSelectionChanged();
        Add(paths);
        Filter();
    }

    /// <summary>Files and folders to send, in the order they were added.</summary>
    public ObservableCollection<string> Paths { get; } = [];

    /// <summary>People matching <see cref="Search"/>.</summary>
    public ObservableCollection<QuickSendPerson> People { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    public bool HasPaths => Paths.Count > 0;

    public bool HasNobody => everyone.Count == 0;

    public string PathsHeader => Paths.Count == 0 ? "Thả file hoặc thư mục vào đây" : $"{Paths.Count} mục sẽ gửi";

    public string SummaryText => $"Đã chọn {everyone.Count(p => p.IsChecked)} người";

    public bool CanSend => Paths.Count > 0 && everyone.Any(p => p.IsChecked);

    /// <summary>Set when the user presses "Gửi": what to send, one offer per batch.</summary>
    public (IReadOnlyList<string> Paths, IReadOnlyList<OfferBatch> Batches)? Result { get; private set; }

    public void Add(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                Paths.Add(path);
            }
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private void Clear() => Paths.Clear();

    [RelayCommand]
    private void Send()
    {
        if (!CanSend)
        {
            return;
        }

        OfferBatch[] batches =
        [
            .. everyone.Where(p => p.IsChecked)
                .GroupBy(p => p.GroupId)
                .Select(g => new OfferBatch([.. g.Select(p => p.DeviceId)], g.Key)),
        ];
        Result = ([.. Paths], batches);
        RequestClose();
    }

    [RelayCommand]
    private void Cancel() => RequestClose();

    private void Filter()
    {
        string search = Search.Trim();
        People.Clear();
        foreach (QuickSendPerson person in everyone)
        {
            if (search.Length == 0
                || person.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || person.Detail.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            {
                People.Add(person);
            }
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasPaths));
        OnPropertyChanged(nameof(PathsHeader));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanSend));
    }
}
