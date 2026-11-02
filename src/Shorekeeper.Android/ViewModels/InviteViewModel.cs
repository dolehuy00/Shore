using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.ViewModels;

/// <summary>"Mời…" on a group we host (docs/20-phone-host.md, H2): pick people online who are not in it yet.</summary>
public sealed partial class InviteViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<PickablePerson>, Task> invite;
    private readonly MainViewModel owner;

    public InviteViewModel(string groupName, IReadOnlyList<PickablePerson> people, Func<IReadOnlyList<PickablePerson>, Task> invite, MainViewModel owner)
    {
        this.invite = invite;
        this.owner = owner;
        Title = L.F("Mời vào nhóm {0}", groupName);
        People = people;
        foreach (PickablePerson person in people)
        {
            person.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanInvite));
        }
    }

    public string Title { get; }

    public IReadOnlyList<PickablePerson> People { get; }

    public bool HasNobody => People.Count == 0;

    public bool CanInvite => People.Any(p => p.IsChecked);

    [RelayCommand]
    private async Task InviteAsync()
    {
        owner.Invite = null;
        await invite([.. People.Where(p => p.IsChecked)]);
    }

    [RelayCommand]
    private void Cancel() => owner.Invite = null;
}
