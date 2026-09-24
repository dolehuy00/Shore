using System.Globalization;
using Microsoft.Win32;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Reads GPO values from <c>Software\Policies\Shorekeeper</c>; machine policy (HKLM) wins over user policy (HKCU).
/// Values are read on every call so policy refreshes (gpupdate) are picked up without restarting.
/// </summary>
public sealed class RegistryPolicyProvider : IPolicyProvider
{
    public const string PolicyKeyPath = @"Software\Policies\Shorekeeper";

    public string? GetString(string name) => Read(name) switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s,
        _ => null,
    };

    public int? GetInt32(string name) => Read(name) switch
    {
        int i => i,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => null,
    };

    public bool? GetBoolean(string name) => GetInt32(name) is { } value ? value != 0 : null;

    public IReadOnlyList<string>? GetStringList(string name) => Read(name) switch
    {
        string[] items => [.. items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim())],
        string s when !string.IsNullOrWhiteSpace(s) => [.. s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        _ => null,
    };

    private static object? Read(string name)
    {
        foreach (RegistryKey hive in (ReadOnlySpan<RegistryKey>)[Registry.LocalMachine, Registry.CurrentUser])
        {
            using RegistryKey? key = hive.OpenSubKey(PolicyKeyPath);
            if (key?.GetValue(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }
}
