using System.Buffers.Binary;
using TextEncoding = System.Text.Encoding;

namespace Shorekeeper.Core.Audio;

/// <summary>Opus packets with what decoding them needs, as read from an Ogg Opus file.</summary>
/// <param name="PreSkip">Samples at 48 kHz to drop at the start: the encoder's look-ahead.</param>
public sealed record OpusClip(int Channels, int PreSkip, IReadOnlyList<byte[]> Packets);

/// <summary>
/// The Ogg Opus file format (RFC 7845), enough for voice messages (docs/15-chat.md §3): one logical stream, an
/// identification header, a comment header, then audio pages of up to a second of packets. Saved as .ogg, the files
/// open in other players too.
/// </summary>
public static class OggOpus
{
    private const int PacketsPerPage = 50;
    private const byte FirstPage = 0x02;
    private const byte LastPage = 0x04;
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="samplesPerPacket">Samples at 48 kHz in each packet, for the pages' granule positions.</param>
    public static byte[] Write(OpusClip clip, int samplesPerPacket)
    {
        using var output = new MemoryStream();
        int serial = Random.Shared.Next();
        uint sequence = 0;

        byte[] head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)clip.Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)clip.PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        WritePage(output, [head], FirstPage, 0, serial, sequence++);

        byte[] vendor = TextEncoding.UTF8.GetBytes("Shorekeeper");
        byte[] tags = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        WritePage(output, [tags], 0, 0, serial, sequence++);

        long granule = 0;
        for (int i = 0; i < clip.Packets.Count || i == 0; i += PacketsPerPage)
        {
            byte[][] packets = [.. clip.Packets.Skip(i).Take(PacketsPerPage)];
            granule += (long)packets.Length * samplesPerPacket;
            bool last = i + PacketsPerPage >= clip.Packets.Count;
            WritePage(output, packets, last ? LastPage : (byte)0, granule, serial, sequence++);
        }

        return output.ToArray();
    }

    /// <returns>Null when this is not an Ogg Opus file this reader understands, or it is damaged.</returns>
    public static OpusClip? Read(ReadOnlySpan<byte> file)
    {
        List<byte[]> packets = [];
        using var partial = new MemoryStream();
        int offset = 0;
        while (offset < file.Length)
        {
            if (file.Length - offset < 27 || !file.Slice(offset, 4).SequenceEqual("OggS"u8) || file[offset + 4] != 0)
            {
                return null;
            }

            int segments = file[offset + 26];
            if (file.Length - offset < 27 + segments)
            {
                return null;
            }

            ReadOnlySpan<byte> table = file.Slice(offset + 27, segments);
            int size = 27 + segments;
            foreach (byte lacing in table)
            {
                size += lacing;
            }

            if (file.Length - offset < size)
            {
                return null;
            }

            Span<byte> page = file.Slice(offset, size).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(page[22..]);
            page[22..26].Clear();
            if (Crc(page) != crc)
            {
                return null;
            }

            int data = offset + 27 + segments;
            foreach (byte lacing in table)
            {
                partial.Write(file.Slice(data, lacing));
                data += lacing;
                if (lacing < 255)
                {
                    packets.Add(partial.ToArray());
                    partial.SetLength(0);
                }
            }

            offset += size;
        }

        if (packets.Count < 2 || packets[0].Length < 19 || !packets[0].AsSpan(0, 8).SequenceEqual("OpusHead"u8)
            || packets[0][9] is not (1 or 2) || !packets[1].AsSpan().StartsWith("OpusTags"u8))
        {
            return null;
        }

        return new OpusClip(packets[0][9], BinaryPrimitives.ReadUInt16LittleEndian(packets[0].AsSpan(10)), packets.GetRange(2, packets.Count - 2));
    }

    private static void WritePage(Stream output, IReadOnlyList<byte[]> packets, byte flags, long granule, int serial, uint sequence)
    {
        List<byte> table = [];
        foreach (byte[] packet in packets)
        {
            // A packet is 255-byte segments, then a shorter one (0 when it ends on a boundary).
            for (int left = packet.Length; ; left -= 255)
            {
                table.Add((byte)Math.Min(left, 255));
                if (left < 255)
                {
                    break;
                }
            }
        }

        if (table.Count > 255)
        {
            throw new InvalidOperationException("Too many segments for one Ogg page.");
        }

        byte[] page = new byte[27 + table.Count + packets.Sum(p => p.Length)];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)table.Count;
        table.CopyTo(page, 27);
        int at = 27 + table.Count;
        foreach (byte[] packet in packets)
        {
            packet.CopyTo(page, at);
            at += packet.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        output.Write(page);
    }

    /// <summary>Ogg's CRC-32: polynomial 0x04C11DB7, not reflected, starting at 0.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                r = (r & 0x8000_0000) != 0 ? (r << 1) ^ 0x04C1_1DB7 : r << 1;
            }

            table[i] = r;
        }

        return table;
    }
}
