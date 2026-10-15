using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Hosting;

namespace Shorekeeper.Engine.Tests;

public sealed class EngineStartupTests : IDisposable
{
    private readonly TempAppFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task Host_start_initializes_engine_and_identity_survives_restart()
    {
        DeviceId first = await StartAndGetDeviceId();
        DeviceId second = await StartAndGetDeviceId();

        Assert.False(first.IsEmpty);
        Assert.Equal(first, second);
        Assert.True(File.Exists(folder.Paths.DatabaseFile));
    }

    [Fact(Skip = "Windows protects the key with DPAPI instead", SkipType = typeof(TestPlatform), SkipWhen = nameof(TestPlatform.IsWindows))]
    [UnsupportedOSPlatform("windows")]
    public void Data_folder_is_readable_by_its_owner_only()
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder.Paths.DataDirectory));
    }

    private async Task<DeviceId> StartAndGetDeviceId()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddShorekeeperEngine(folder.Paths);
        builder.Services.AddSingleton<ISecretProtector, XorSecretProtector>();

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var engine = host.Services.GetRequiredService<ShorekeeperEngine>();
        DeviceId id = engine.Identity.DeviceId;
        await host.StopAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
