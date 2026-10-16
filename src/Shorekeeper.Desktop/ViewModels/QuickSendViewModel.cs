using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;

namespace Shorekeeper.Desktop.ViewModels;

/// <param name="groupId">Null for contacts; for fellow members, the group the offer goes through.</param>
public sealed partial class QuickSendPerson(DeviceId deviceId, string name, string? groupId) : ObservableObject
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public string? GroupId { get; } = groupId;

    public bool IsPhone { get; init; }

    public bool IsBusy { get; init; }

    /// <summary>Gray dot: offline, or no longer heard.</summary>
    public bool IsInactive { get; init; }

    /// <summary>"Online", "Bận", "Offline".</summary>
    public string StatusText { get; init; } = "";

    /// <summary>The computer's name, then when files last went to them: "MSI-PC · gửi lần cuối hôm qua".</summary>
    public string Detail { get; init; } = "";

    /// <summary>"Nhóm Kế toán" for fellow members who are not contacts.</summary>
    public string GroupText { get; init; } = "";

    public bool HasGroup => GroupText.Length > 0;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>A file or folder to send: its name, and the folder it is in below it.</summary>
public sealed class QuickSendFile(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    public string Folder { get; } = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path)) ?? "";

    public bool IsFolder { get; } = Directory.Exists(path);
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

        Files.CollectionChanged += (_, _) => OnSelectionChanged();
        Add(paths);
        Filter();
    }

    /// <summary>Files and folders to send, in the order they were added.</summary>
    public ObservableCollection<QuickSendFile> Files { get; } = [];

    /// <summary>People matching <see cref="Search"/>.</summary>
    public ObservableCollection<QuickSendPerson> People { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    public bool HasPaths => Files.Count > 0;

    public bool HasNobody => everyone.Count == 0;

    public string PathsHeader => Files.Count == 0 ? L.T("Thả file hoặc thư mục vào đây") : L.F("{0} mục sẽ gửi", Files.Count);

    public string SummaryText => L.F("Đã chọn {0} người", everyone.Count(p => p.IsChecked));

    public bool CanSend => Files.Count > 0 && everyone.Any(p => p.IsChecked);

    /// <summary>Set when the user presses "Gửi": what to send, one offer per batch.</summary>
    public (IReadOnlyList<string> Paths, IReadOnlyList<OfferBatch> Batches)? Result { get; private set; }

    public void Add(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (!Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Files.Add(new QuickSendFile(path));
            }
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private void Clear() => Files.Clear();

    /// <summary>The ✕ on one row.</summary>
    [RelayCommand]
    private void Remove(QuickSendFile file) => Files.Remove(file);

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
        Result = ([.. Files.Select(f => f.Path)], batches);
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
                || person.Detail.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || person.GroupText.Contains(search, StringComparison.CurrentCultureIgnoreCase))
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
