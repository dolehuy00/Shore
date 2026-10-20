namespace Shorekeeper.Core.Platform;

/// <summary>A media key, as a listener presses it on the source ("Điều khiển" while listening).</summary>
public enum MediaKey
{
    PlayPause,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    Mute,
}

/// <summary>Presses media keys on this device, as if on its keyboard: whatever plays reacts (Windows).</summary>
public interface IMediaKeys
{
    void Press(MediaKey key);
}

/// <summary>The keys' names on the wire (docs/05-protocol.md §4.7).</summary>
public static class MediaKeyNames
{
    private static readonly Dictionary<MediaKey, string> Names = new()
    {
        [MediaKey.PlayPause] = "play-pause",
        [MediaKey.Next] = "next",
        [MediaKey.Previous] = "previous",
        [MediaKey.VolumeUp] = "volume-up",
        [MediaKey.VolumeDown] = "volume-down",
        [MediaKey.Mute] = "mute",
    };

    public static string Of(MediaKey key) => Names[key];

    public static bool TryParse(string? name, out MediaKey key)
    {
        foreach ((MediaKey candidate, string candidateName) in Names)
        {
            if (candidateName == name)
            {
                key = candidate;
                return true;
            }
        }

        key = default;
        return false;
    }
}
