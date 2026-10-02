using Shorekeeper.Core.Discovery;
using TextEncoding = System.Text.Encoding;

namespace Shorekeeper.Core.Tests;

public class PresenceCodecTests
{
    private static readonly string ValidId = new('a', 52);

    [Fact]
    public void Encode_then_decode_round_trips()
    {
        var packet = new PresencePacket
        {
            Type = PresencePacketTypes.Announce,
            Id = ValidId,
            Name = "Lê Huy · PC-DEV-03",
            Host = "PC-DEV-03",
            Os = "windows",
            App = "0.1.0",
            Port = 47471,
            Addresses = ["10.1.1.23", "192.168.1.5"],
            Status = PresenceStatus.Busy,
            Capabilities = [PresenceCapabilities.Bridge],
        };

        byte[] data = PresenceCodec.Encode(packet);

        Assert.True(data.AsSpan().StartsWith("SKP1"u8));
        Assert.True(PresenceCodec.TryDecode(data, out PresencePacket? decoded));
        Assert.Equal(packet.Name, decoded.Name);
        Assert.Equal(packet.Addresses, decoded.Addresses);
        Assert.Equal(packet.Capabilities, decoded.Capabilities);
        Assert.Equal(packet with { Addresses = decoded.Addresses, Capabilities = decoded.Capabilities }, decoded);
    }

    [Fact]
    public void Decode_normalizes_device_id_case()
    {
        byte[] data = Packet($$"""{"v":1,"type":"heartbeat","id":"{{ValidId.ToUpperInvariant()}}"}""");

        Assert.True(PresenceCodec.TryDecode(data, out PresencePacket? decoded));
        Assert.Equal(ValidId, decoded.Id);
    }

    [Fact]
    public void Decode_fills_defaults_for_missing_fields()
    {
        byte[] data = Packet($$"""{"v":1,"type":"heartbeat","id":"{{ValidId}}"}""");

        Assert.True(PresenceCodec.TryDecode(data, out PresencePacket? decoded));
        Assert.Equal("", decoded.Name);
        Assert.Equal("", decoded.Host);
        Assert.Empty(decoded.Addresses);
        Assert.Empty(decoded.Capabilities);
        Assert.Equal(PresenceStatus.Available, decoded.Status);
    }

    [Theory]
    [InlineData("""{"v":1,"type":"heartbeat"}""")] // missing id
    [InlineData("""{"v":2,"type":"heartbeat","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")] // unknown version
    [InlineData("""{"v":1,"type":"hack","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")] // unknown type
    [InlineData("""{"v":1,"type":"heartbeat","id":"not-a-device-id"}""")]
    [InlineData("""{ broken json""")]
    public void Decode_rejects_invalid_json(string json)
    {
        Assert.False(PresenceCodec.TryDecode(Packet(json), out _));
    }

    [Fact]
    public void Decode_rejects_wrong_magic_and_oversized_packets()
    {
        byte[] valid = PresenceCodec.Encode(new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = ValidId });
        byte[] wrongMagic = [.. "XXXX"u8, .. valid.AsSpan(4)];
        byte[] oversized = [.. valid, .. new byte[PresenceCodec.MaxPacketSize]];

        Assert.False(PresenceCodec.TryDecode(wrongMagic, out _));
        Assert.False(PresenceCodec.TryDecode(oversized, out _));
        Assert.False(PresenceCodec.TryDecode([], out _));
    }

    [Fact]
    public void Decode_rejects_names_longer_than_limit()
    {
        string name = new('x', PresenceCodec.MaxNameLength + 1);
        byte[] data = Packet($$"""{"v":1,"type":"heartbeat","id":"{{ValidId}}","name":"{{name}}"}""");

        Assert.False(PresenceCodec.TryDecode(data, out _));
    }

    private static byte[] Packet(string json) => [.. "SKP1"u8, 0, 0, .. TextEncoding.UTF8.GetBytes(json)];
}
