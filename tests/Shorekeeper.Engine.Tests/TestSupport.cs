using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Tests;

/// <summary>Isolated data folder for one test; deleted afterwards.</summary>
internal sealed class TempAppFolder : IDisposable
{
    public TempAppFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "shorekeeper-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "downloads"));
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        // Pooled SQLite connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it.
        }
    }
}

/// <summary>Reversible, non-secret "protection" so tests do not depend on DPAPI.</summary>
internal sealed class XorSecretProtector : ISecretProtector
{
    public int ProtectCalls { get; private set; }

    public int UnprotectCalls { get; private set; }

    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        return Xor(plaintext);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        UnprotectCalls++;
        return Xor(protectedData);
    }

    private static byte[] Xor(byte[] data) => [.. data.Select(b => (byte)(b ^ 0x5A))];
}
