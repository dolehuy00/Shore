using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Core.Discovery;

/// <summary>
/// Wire format: "SKP1" magic, 2 reserved bytes, then UTF-8 JSON. At most 1200 bytes to avoid IP fragmentation.
/// </summary>
public static class PresenceCodec
{
    public const int MaxPacketSize = 1200;
    public const int MaxNameLength = 64;

    private const int HeaderLength = 6;
    private static ReadOnlySpan<byte> Magic => "SKP1"u8;

    public static byte[] Encode(PresencePacket packet)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(packet, PresenceJsonContext.Default.PresencePacket);
        if (HeaderLength + json.Length > MaxPacketSize)
        {
            throw new InvalidOperationException($"Presence packet is {HeaderLength + json.Length} bytes; the limit is {MaxPacketSize}.");
        }

        byte[] buffer = new byte[HeaderLength + json.Length];
        Magic.CopyTo(buffer);
        json.CopyTo(buffer, HeaderLength);
        return buffer;
    }

    /// <summary>Decodes and validates a packet. Anything unexpected is rejected rather than partially trusted.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PresencePacket? packet)
    {
        packet = null;
        if (data.Length <= HeaderLength || data.Length > MaxPacketSize || !data.StartsWith(Magic))
        {
            return false;
        }

        PresencePacket? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize(data[HeaderLength..], PresenceJsonContext.Default.PresencePacket);
        }
        catch (JsonException)
        {
            return false;
        }

        // Fields missing from the JSON come back as null despite the record's defaults; peers may omit them.
        string name = (decoded?.Name ?? "").Trim();
        if (decoded is null
            || decoded.Version != PresencePacket.CurrentVersion
            || !PresencePacketTypes.IsKnown(decoded.Type)
            || !DeviceId.TryParse(decoded.Id, out DeviceId id)
            || name.Length > MaxNameLength)
        {
            return false;
        }

        packet = decoded with
        {
            Id = id.Value,
            Name = name,
            Host = decoded.Host ?? "",
            Os = decoded.Os ?? "",
            App = decoded.App ?? "",
            Addresses = decoded.Addresses ?? [],
            Status = decoded.Status ?? PresenceStatus.Available,
        };
        return true;
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(PresencePacket))]
internal sealed partial class PresenceJsonContext : JsonSerializerContext;
