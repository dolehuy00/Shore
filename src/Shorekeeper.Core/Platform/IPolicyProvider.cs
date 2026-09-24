namespace Shorekeeper.Core.Platform;

/// <summary>
/// Read-only administrator policies (GPO on Windows). A non-null value means the
/// setting is enforced by IT and must not be changed by the user.
/// Names are listed in <see cref="Settings.PolicyNames"/>.
/// </summary>
public interface IPolicyProvider
{
    string? GetString(string name);

    int? GetInt32(string name);

    bool? GetBoolean(string name);

    IReadOnlyList<string>? GetStringList(string name);
}

/// <summary>No policies: every setting is controlled by the user.</summary>
public sealed class NullPolicyProvider : IPolicyProvider
{
    public static NullPolicyProvider Instance { get; } = new();

    public string? GetString(string name) => null;

    public int? GetInt32(string name) => null;

    public bool? GetBoolean(string name) => null;

    public IReadOnlyList<string>? GetStringList(string name) => null;
}
