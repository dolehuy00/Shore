using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace A0;

/// <summary>Lines shown in the list (T5) and written to logcat with tag "A0".</summary>
internal static class SpikeLog
{
    public static ObservableCollection<string> Lines { get; } = [];

    public static void Write(string line)
    {
        Android.Util.Log.Info("A0", line);
        Dispatcher.UIThread.Post(() => Lines.Add($"{DateTime.Now:HH:mm:ss} {line}"));
    }
}
