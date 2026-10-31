using CommunityToolkit.Mvvm.ComponentModel;

namespace Shorekeeper.UI.ViewModels;

/// <summary>Something in the open conversation, in time order: a message or a file card.</summary>
public abstract partial class ChatEntryViewModel(string id) : ObservableObject
{
    /// <summary>Called by the clock: "Thu hồi" goes once its time is up.</summary>
    internal virtual void RefreshRecall()
    {
    }

    public string Id { get; } = id;

    /// <summary>"Hủy gửi" or "Thu hồi" (<see cref="ChatViewModel.RecallLabel"/>).</summary>
    [ObservableProperty]
    public partial string RecallText { get; set; } = "";

    /// <summary>Set on the first entry of a day: "Hôm nay", "20/10/2026".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDay))]
    public partial string? DayText { get; set; }

    public bool HasDay => DayText is not null;
}
