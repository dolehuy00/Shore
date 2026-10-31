using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Shorekeeper.Android.ViewModels;

/// <summary>A question over the page: a confirmation, or a name to type when <paramref name="input"/> is set.</summary>
public sealed partial class QuestionViewModel(string title, string? message, string okText, string? input, Func<string, Task> ok, MainViewModel owner)
    : ObservableObject
{
    public string Title { get; } = title;

    public string? Message { get; } = message;

    public bool HasMessage => Message is not null;

    public string OkText { get; } = okText;

    public bool HasInput { get; } = input is not null;

    /// <summary>The input is a password: what is typed is hidden.</summary>
    public bool IsPassword { get; init; }

    public bool HasTextInput => HasInput && !IsPassword;

    public bool HasPasswordInput => HasInput && IsPassword;

    /// <summary>Called when the user closes the question without answering.</summary>
    public Action? Cancelled { get; init; }

    [ObservableProperty]
    public partial string Input { get; set; } = input ?? "";

    [RelayCommand]
    private async Task OkAsync()
    {
        owner.Question = null;
        await ok(Input.Trim());
    }

    [RelayCommand]
    private void Cancel()
    {
        owner.Question = null;
        Cancelled?.Invoke();
    }
}
