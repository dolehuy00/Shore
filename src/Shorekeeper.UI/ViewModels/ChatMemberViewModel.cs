using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Shorekeeper.UI.ViewModels;

/// <summary>A member in "Chi tiết" or "Thành viên": who, and where the message is with them or whether they are there.</summary>
/// <param name="rename">"Đặt tên gợi nhớ…" for that member; null for ourselves.</param>
public sealed partial class ChatMemberViewModel(string name, string stateText, bool isOnline, Func<Task>? retry, Func<Task>? rename = null) : ObservableObject
{
    public string Name { get; } = name;

    public string StateText { get; } = stateText;

    public bool IsOnline { get; } = isOnline;

    /// <summary>It failed with them (or they had an old version): "Gửi lại".</summary>
    public bool CanRetry => retry is not null;

    [RelayCommand]
    private Task RetryAsync() => retry?.Invoke() ?? Task.CompletedTask;

    public bool CanRename => rename is not null;

    [RelayCommand]
    private Task RenameAsync() => rename?.Invoke() ?? Task.CompletedTask;
}
