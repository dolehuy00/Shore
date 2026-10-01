using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Hosting;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>A real engine with its own identity, database and HTTPS API on loopback.</summary>
internal sealed class TestNode : IAsyncDisposable
{
    private readonly TempAppFolder folder;
    private readonly IHost host;

    private TestNode(TempAppFolder folder, IHost host)
    {
        this.folder = folder;
        this.host = host;
    }

    public DeviceId Id => Get<ShorekeeperEngine>().Identity.DeviceId;

    public int ApiPort => Get<ApiServer>().Port;

    public TrustStore Trust => Get<TrustStore>();

    public PairingService Pairing => Get<PairingService>();

    public PeerClient Client => Get<PeerClient>();

    public PeerDirectory Directory => Get<PeerDirectory>();

    public static async Task<TestNode> StartAsync(string name)
    {
        var folder = new TempAppFolder();
        // A private port per node so tests never collide with a running Shorekeeper.
        File.WriteAllText(folder.Paths.SettingsFile, $$"""{ "displayName": "{{name}}", "apiPort": {{Random.Shared.Next(45000, 50000)}} }""");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddSingleton(new ApiServerOptions { BindAddress = IPAddress.Loopback });
        builder.Services.AddSingleton<ISecretProtector, XorSecretProtector>();
        builder.Services.AddShorekeeperEngine(folder.Paths);
        builder.Services.AddPeerApi();

        IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new TestNode(folder, host);
    }

    public T Get<T>()
        where T : notnull => host.Services.GetRequiredService<T>();

    /// <summary>What discovery would do: learn where <paramref name="other"/> listens.</summary>
    public void Sees(TestNode other) => Directory.Observe(
        new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = other.Id.Value, Name = "peer", Port = other.ApiPort },
        IPAddress.Loopback);

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
        folder.Dispose();
    }
}
