using System.Reflection;
using Shorekeeper.Core.Discovery;
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

public sealed class LocalPresence(ShorekeeperEngine engine, SettingsService settings) : ILocalPresence
{
    private static readonly string AppVersion =
        typeof(LocalPresence).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private MyStatus status = MyStatus.Online;

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
        Id = engine.Identity.DeviceId.Value,
        Name = settings.Current.DisplayName.Length > PresenceCodec.MaxNameLength
            ? settings.Current.DisplayName[..PresenceCodec.MaxNameLength]
            : settings.Current.DisplayName,
        Host = Environment.MachineName,
        Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        App = AppVersion,
        Port = settings.Current.ApiPort,
        Status = status == MyStatus.Busy ? PresenceStatus.Busy : PresenceStatus.Available,
    };
}
