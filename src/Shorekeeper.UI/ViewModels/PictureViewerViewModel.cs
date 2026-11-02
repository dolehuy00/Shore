using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// Pictures looked at in the app (docs/19-transfers-preview.md §4), one at a time: decoded no bigger than the screen, the
/// ones beside it loaded ahead, the others let go.
/// </summary>
public sealed partial class PictureViewerViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<string> locations;
    private readonly Func<string, Stream> open;
    private readonly Func<string, string> nameOf;
    private readonly Func<string, bool>? isPreview;
    private readonly int maxWidth;
    private readonly int maxHeight;
    private readonly Dictionary<int, Task<Bitmap?>> loaded = [];
    private bool disposed;

    /// <param name="open">Reads a picture where it is: a path, or a location of the OS such as content://.</param>
    /// <param name="isPreview">Whether a location is the small copy of a picture not downloaded (docs/19-transfers-preview.md §7).</param>
    public PictureViewerViewModel(
        IReadOnlyList<string> locations, int index, Func<string, Stream> open, Func<string, string> nameOf, int maxWidth, int maxHeight,
        Func<string, bool>? isPreview = null)
    {
        this.locations = locations;
        this.open = open;
        this.nameOf = nameOf;
        this.isPreview = isPreview;
        this.maxWidth = maxWidth;
        this.maxHeight = maxHeight;
        Index = Math.Clamp(index, 0, Math.Max(0, locations.Count - 1));
        _ = ShowAsync();
    }

    public int Count => locations.Count;

    /// <summary>Where the picture shown is: a path, or a location of the OS.</summary>
    public string Location => locations[Index];

    public string Name => nameOf(Location);

    /// <summary>"3 / 48"; empty for a single picture.</summary>
    public string CountText => Count > 1 ? $"{Index + 1} / {Count}" : "";

    public bool CanPrevious => Index > 0;

    public bool CanNext => Index < Count - 1;

    /// <summary>The small copy of a picture not downloaded: there is no file of it to show in a folder or open elsewhere.</summary>
    public bool IsPreview => isPreview?.Invoke(Location) ?? false;

    public bool IsOriginal => !IsPreview;

    public string PreviewText => IsPreview ? L.T("Ảnh xem trước · bấm Tải để lấy ảnh gốc") : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Location), nameof(Name), nameof(CountText), nameof(CanPrevious), nameof(CanNext), nameof(IsPreview), nameof(IsOriginal), nameof(PreviewText))]
    public partial int Index { get; set; }

    /// <summary>Null while loading, or when it cannot be shown.</summary>
    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    /// <summary>A kind the decoder does not know (HEIC, RAW), or gone: open it with another app instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotShowText))]
    public partial bool CannotShow { get; set; }

    public string CannotShowText => CannotShow ? L.T("Không xem được ảnh này trong app.") : "";

    [RelayCommand]
    private void Previous() => Go(Index - 1);

    [RelayCommand]
    private void Next() => Go(Index + 1);

    private void Go(int index)
    {
        if (index < 0 || index >= Count || index == Index)
        {
            return;
        }

        Index = index;
        _ = ShowAsync();
    }

    private async Task ShowAsync()
    {
        int at = Index;
        Bitmap? image = await Load(at);
        if (disposed || at != Index)
        {
            return;
        }

        Image = image;
        CannotShow = image is null;
        _ = Load(at - 1);
        _ = Load(at + 1);

        // Only the one shown and those beside it stay in memory.
        foreach (int far in loaded.Keys.Where(i => Math.Abs(i - at) > 1).ToList())
        {
            Task<Bitmap?> task = loaded[far];
            loaded.Remove(far);
            _ = task.ContinueWith(t => (t.IsCompletedSuccessfully ? t.Result : null)?.Dispose(), TaskScheduler.Default);
        }
    }

    private Task<Bitmap?> Load(int index)
    {
        if (index < 0 || index >= Count)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        if (!loaded.TryGetValue(index, out Task<Bitmap?>? task))
        {
            string location = locations[index];
            task = Task.Run(() =>
            {
                try
                {
                    using Stream stream = open(location);
                    return (Bitmap?)PictureLoader.Decode(stream, maxWidth, maxHeight);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return null;
                }
            });
            loaded[index] = task;
        }

        return task;
    }

    /// <summary>Lets every picture go; call once nothing shows them any more.</summary>
    public void Dispose()
    {
        disposed = true;
        Image = null;
        foreach (Task<Bitmap?> task in loaded.Values)
        {
            _ = task.ContinueWith(t => (t.IsCompletedSuccessfully ? t.Result : null)?.Dispose(), TaskScheduler.Default);
        }

        loaded.Clear();
    }
}
