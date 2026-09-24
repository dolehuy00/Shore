using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;

namespace Shorekeeper.Core.Tests;

public class SettingsResolverTests
{
    private const string DefaultName = "huy · PC-DEV-03";
    private const string DefaultDownloads = @"C:\Users\huy\Downloads\Shorekeeper";

    [Fact]
    public void Defaults_apply_when_nothing_is_set()
    {
        EffectiveSettings settings = Resolve(new ShorekeeperSettings());

        Assert.Equal(DefaultName, settings.DisplayName);
        Assert.Equal(DefaultDownloads, settings.DownloadDirectory);
        Assert.Equal(TimeSpan.FromHours(24), settings.OfferLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.ReconnectWindow);
        Assert.Equal(47470, settings.DiscoveryPort);
        Assert.Equal(47471, settings.ApiPort);
        Assert.Empty(settings.LockedSettings);
    }

    [Fact]
    public void User_values_override_defaults()
    {
        EffectiveSettings settings = Resolve(new ShorekeeperSettings
        {
            DisplayName = "  Lê Huy  ",
            DownloadDirectory = @"D:\Nhan",
            OfferLifetimeHours = 6,
            ReconnectWindowSeconds = 120,
        });

        Assert.Equal("Lê Huy", settings.DisplayName);
        Assert.Equal(@"D:\Nhan", settings.DownloadDirectory);
        Assert.Equal(TimeSpan.FromHours(6), settings.OfferLifetime);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.ReconnectWindow);
    }

    [Fact]
    public void Policy_overrides_user_and_locks_the_setting()
    {
        var policy = new FakePolicy
        {
            [PolicyNames.DownloadDirectory] = @"E:\Shared\Inbox",
            [PolicyNames.ReconnectWindowSeconds] = 30,
            [PolicyNames.ApiPort] = 50000,
        };

        EffectiveSettings settings = Resolve(
            new ShorekeeperSettings { DownloadDirectory = @"D:\Nhan", ReconnectWindowSeconds = 200, ApiPort = 1234 },
            policy);

        Assert.Equal(@"E:\Shared\Inbox", settings.DownloadDirectory);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.ReconnectWindow);
        Assert.Equal(50000, settings.ApiPort);
        Assert.True(settings.IsLocked(nameof(ShorekeeperSettings.DownloadDirectory)));
        Assert.True(settings.IsLocked(nameof(ShorekeeperSettings.ReconnectWindowSeconds)));
        Assert.True(settings.IsLocked(nameof(ShorekeeperSettings.ApiPort)));
        Assert.False(settings.IsLocked(nameof(ShorekeeperSettings.DiscoveryPort)));
    }

    [Fact]
    public void Max_offer_lifetime_policy_is_a_ceiling()
    {
        var policy = new FakePolicy { [PolicyNames.MaxOfferLifetimeHours] = 12 };

        Assert.Equal(TimeSpan.FromHours(12), Resolve(new ShorekeeperSettings { OfferLifetimeHours = 48 }, policy).OfferLifetime);
        Assert.Equal(TimeSpan.FromHours(2), Resolve(new ShorekeeperSettings { OfferLifetimeHours = 2 }, policy).OfferLifetime);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(1000, 300)]
    public void Reconnect_window_is_clamped(int seconds, int expected)
    {
        EffectiveSettings settings = Resolve(new ShorekeeperSettings { ReconnectWindowSeconds = seconds });

        Assert.Equal(TimeSpan.FromSeconds(expected), settings.ReconnectWindow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void Invalid_ports_fall_back_to_default(int port)
    {
        EffectiveSettings settings = Resolve(new ShorekeeperSettings { DiscoveryPort = port, ApiPort = port });

        Assert.Equal(EffectiveSettings.DefaultDiscoveryPort, settings.DiscoveryPort);
        Assert.Equal(EffectiveSettings.DefaultApiPort, settings.ApiPort);
    }

    private static EffectiveSettings Resolve(ShorekeeperSettings user, IPolicyProvider? policy = null) =>
        SettingsResolver.Resolve(user, policy ?? NullPolicyProvider.Instance, DefaultName, DefaultDownloads);

    private sealed class FakePolicy : Dictionary<string, object>, IPolicyProvider
    {
        public string? GetString(string name) => TryGetValue(name, out object? v) ? v as string : null;

        public int? GetInt32(string name) => TryGetValue(name, out object? v) && v is int i ? i : null;

        public bool? GetBoolean(string name) => GetInt32(name) is { } i ? i != 0 : null;

        public IReadOnlyList<string>? GetStringList(string name) => TryGetValue(name, out object? v) ? v as string[] : null;
    }
}
