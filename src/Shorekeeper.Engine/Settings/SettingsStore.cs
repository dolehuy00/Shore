using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Settings;

namespace Shorekeeper.Engine.Settings;

/// <summary>Reads and writes the user's <c>settings.json</c>.</summary>
public sealed class SettingsStore(AppPaths paths, TimeProvider timeProvider, ILogger<SettingsStore> logger)
{
    public ShorekeeperSettings Load()
    {
        string path = paths.SettingsFile;
        if (!File.Exists(path))
        {
            return new ShorekeeperSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.ShorekeeperSettings) ?? new ShorekeeperSettings();
        }
        catch (JsonException ex)
        {
            string backup = $"{path}.broken-{timeProvider.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(path, backup);
            logger.LogWarning(ex, "settings.json is invalid and was moved to {Backup}. Using defaults.", backup);
            return new ShorekeeperSettings();
        }
    }

    public void Save(ShorekeeperSettings settings)
    {
        string path = paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (FileStream stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.ShorekeeperSettings);
        }

        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ShorekeeperSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
