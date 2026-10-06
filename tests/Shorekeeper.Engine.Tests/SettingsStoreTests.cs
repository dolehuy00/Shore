using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempAppFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void Missing_file_gives_defaults()
    {
        Assert.Equal(new ShorekeeperSettings(), CreateStore().Load());
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var settings = new ShorekeeperSettings
        {
            DisplayName = "Lê Huy",
            DownloadDirectory = @"D:\Nhận file",
            OfferLifetimeHours = 6,
            AlwaysSnapshotBeforeSend = true,
        };

        CreateStore().Save(settings);

        Assert.Equal(settings, CreateStore().Load());
    }

    [Fact]
    public void Quick_send_hotkey_turned_off_is_remembered()
    {
        // Files written before the setting existed leave it to the default.
        File.WriteAllText(folder.Paths.SettingsFile, """{ "DisplayName": "Mai" }""");
        Assert.Null(CreateStore().Load().QuickSendHotkey);

        CreateStore().Save(new ShorekeeperSettings { QuickSendHotkey = false });
        Assert.False(CreateStore().Load().QuickSendHotkey);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_are_used()
    {
        File.WriteAllText(folder.Paths.SettingsFile, "{ not json");

        ShorekeeperSettings loaded = CreateStore().Load();

        Assert.Equal(new ShorekeeperSettings(), loaded);
        Assert.False(File.Exists(folder.Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(folder.Paths.DataDirectory, "settings.json.broken-*"));
    }

    [Fact]
    public void Update_persists_and_raises_changed()
    {
        var service = new SettingsService(folder.Paths, CreateStore(), Core.Platform.NullPolicyProvider.Instance);
        service.Load();
        EffectiveSettings? raised = null;
        service.Changed += (_, s) => raised = s;

        service.Update(s => s with { DisplayName = "Mai · PC-QA-07" });

        Assert.Equal("Mai · PC-QA-07", raised?.DisplayName);
        Assert.Equal("Mai · PC-QA-07", CreateStore().Load().DisplayName);
    }

    private SettingsStore CreateStore() => new(folder.Paths, TimeProvider.System, NullLogger<SettingsStore>.Instance);
}
