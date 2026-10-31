using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.ViewModels;

/// <summary>"Gửi cho ai?" after sharing from another app (Gallery, Files…).</summary>
public sealed partial class ShareViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> locations;
    private readonly MainViewModel owner;

    public ShareViewModel(IReadOnlyList<string> locations, string? text, IReadOnlyList<PickablePerson> people, MainViewModel owner)
    {
        this.locations = locations;
        this.owner = owner;
        Text = text ?? "";
        HasText = text is not null;
        People = people;
        foreach (PickablePerson person in people)
        {
            person.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSend));
        }
    }

    public string Title => locations.Count == 0 ? L.T("Gửi văn bản cho…") : L.F("Gửi {0} mục cho…", locations.Count);

    /// <summary>A shared link or text, sent alone or as the note of the shared files; can be edited first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Text { get; set; }

    public bool HasText { get; }

    public IReadOnlyList<PickablePerson> People { get; }

    public bool HasNobody => People.Count == 0;

    public bool CanSend => !IsSending && People.Any(p => p.IsChecked) && (locations.Count > 0 || !string.IsNullOrWhiteSpace(Text));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private async Task SendAsync()
    {
        IsSending = true;
        Error = await owner.SendAsync(locations, [.. People.Where(p => p.IsChecked).Select(p => (p.DeviceId, p.GroupId))], Text);
        IsSending = false;
        if (Error is null)
        {
            owner.CloseShare();
        }
    }

    [RelayCommand]
    private void Cancel() => owner.CloseShare();
}
