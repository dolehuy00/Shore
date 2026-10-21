using Shorekeeper.Core.Discovery;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Discovery;

/// <summary>What this device announces about itself.</summary>
public interface ILocalPresence
{
    MyStatus Status { get; }

    event EventHandler? Changed;

    /// <summary>Builds a packet of the given type; addresses are filled in by the sender.</summary>
    PresencePacket CreatePacket(string type);
}

public sealed class LocalPresence : ILocalPresence
{
    private readonly LocalDevice device;
    private readonly ApiServer api;
    private readonly SettingsService settings;
    private readonly GroupStore groups;
    private readonly DirectoryProfileService profile;
    private MyStatus status = MyStatus.Online;
    private bool playingAudio;

    public LocalPresence(LocalDevice device, ApiServer api, SettingsService settings, GroupStore groups, DirectoryProfileService profile)
    {
        this.device = device;
        this.api = api;
        this.settings = settings;
        this.groups = groups;
        this.profile = profile;
        // Department and picture from the company directory arrive after start-up.
        profile.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        // Others see a new or renamed group (and its member count) right away.
        groups.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        // A new display name is announced right away instead of with the next heartbeat.
        settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    /// <summary>"Phát âm thanh" is on; announced so contacts can offer "Nghe" (set by the audio service).</summary>
    public bool PlayingAudio
    {
        get => playingAudio;
        set
        {
            if (playingAudio != value)
            {
                playingAudio = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public MyStatus Status
    {
        get => status;
        set
        {
            if (status != value)
            {
                status = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public PresencePacket CreatePacket(string type) => new()
    {
        Type = type,
        Id = device.DeviceId.Value,
        Name = device.DisplayName,
        Host = LocalDevice.HostName,
        Os = LocalDevice.Os,
        App = LocalDevice.AppVersion,
        // The port actually bound, which differs from the setting when a second Windows session runs Shorekeeper.
        Port = api.Port,
        Status = status == MyStatus.Busy ? PresenceStatus.Busy : PresenceStatus.Available,
        Capabilities = [.. Capabilities()],
        Groups = settings.Current.GroupsEnabled
            ? [.. groups.GetAll().Where(g => g.Role == GroupRole.Host).Take(PresencePacket.MaxGroups).Select(g => new PresenceGroup(g.Id, g.Name, g.Members.Count))]
            : [],
        Department = profile.Department,
        AvatarTag = profile.AvatarTag,
    };

    private IEnumerable<string> Capabilities()
    {
        yield return PresenceCapabilities.Chat;
        if (settings.Current.GroupsEnabled)
        {
            yield return PresenceCapabilities.GroupChat;
        }

        if (settings.Current.BridgeEnabled)
        {
            yield return PresenceCapabilities.Bridge;
        }

        if (playingAudio)
        {
            yield return PresenceCapabilities.Audio;
            if (settings.User.AudioPasswordHash is not null)
            {
                yield return PresenceCapabilities.AudioLocked;
            }
        }
    }
}
