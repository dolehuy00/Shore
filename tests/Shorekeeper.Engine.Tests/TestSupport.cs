using System.Net;
using System.Net.Sockets;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Tests;

[assembly: AssemblyFixture(typeof(VietnameseText))]

namespace Shorekeeper.Engine.Tests;

/// <summary>The tests check the Vietnamese wording, whatever the language of the machine running them.</summary>
public sealed class VietnameseText
{
    public VietnameseText() => L.IsVietnamese = true;
}

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
        // Pooled SQLite connections keep the file open on Windows. Only this folder's: SqliteConnection.ClearAllPools
        // also closes connections other tests are opening right now.
        var database = new SqliteDatabase(Paths);
        for (int attempt = 1; ; attempt++)
        {
            database.ClearPool();
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                // Work still finishing after the engine stopped can hand a connection back to the pool for a moment.
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                // Best effort; the OS temp cleaner will get it.
                return;
            }
        }
    }
}

/// <summary>Clock that only moves when the test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
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

/// <summary>Ports the OS reports free right now, so parallel tests never collide (Windows answers "access denied" on clashes).</summary>
internal static class FreePort
{
    public static int Tcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static int Udp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>For tests of behavior only Windows has (<c>SkipUnless</c>).</summary>
public static class TestPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
}
