using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Identity;

/// <summary>
/// For platforms without a key store wired up yet (Linux, macOS; libsecret / Keychain later, docs/02-architecture.md §9).
/// Does not encrypt: like SSH keys, the secret is safe because the data folder is readable by its owner only
/// (<see cref="AppPaths.EnsureCreated"/> sets mode 700).
/// </summary>
public sealed class OwnerOnlyFolderProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => [.. plaintext];

    public byte[] Unprotect(byte[] protectedData) => [.. protectedData];
}
