using System.Reflection;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine;

/// <summary>How this device describes itself to peers (presence packets and <c>GET /hello</c>).</summary>
public sealed class LocalDevice(ShorekeeperEngine engine, SettingsService settings)
{
    public static readonly string AppVersion =
        typeof(LocalDevice).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string Os =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public DeviceId DeviceId => engine.Identity.DeviceId;

    public string DisplayName => settings.Current.DisplayName.Length > PresenceCodec.MaxNameLength
        ? settings.Current.DisplayName[..PresenceCodec.MaxNameLength]
        : settings.Current.DisplayName;

    /// <summary>The computer's name; Android sets the phone's name instead (its machine name is "localhost").</summary>
    public static string HostName { get; set; } = Environment.MachineName;
}
