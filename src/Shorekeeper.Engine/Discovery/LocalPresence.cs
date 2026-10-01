using Shorekeeper.Core.Discovery;
using Shorekeeper.Engine.Api;
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
    private MyStatus status = MyStatus.Online;

    public LocalPresence(LocalDevice device, ApiServer api, SettingsService settings)
    {
        this.device = device;
        this.api = api;
        // A new display name is announced right away instead of with the next heartbeat.
        settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

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
    };
}
