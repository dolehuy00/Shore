using System.Buffers.Binary;

namespace Shorekeeper.Core.Audio;

/// <summary>
/// One UDP datagram of the audio stream: a 10 ms Opus frame plus a copy of the frame before it, so a single lost
/// datagram costs nothing. Layout: version (1) · sequence (4) · sender time in µs (8) · frame (2 + n) · previous (2 + n).
/// </summary>
public readonly ref struct AudioPacket
{
    public const byte Version = 1;
    public const int MaxSize = 1200;
    /// <summary>Version, sequence and sender time: readable on the wire, the rest is sealed (<see cref="AudioCipher"/>).</summary>
    public const int HeaderSize = 13;

    public AudioPacket(uint sequence, long sentMicros, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> previous)
    {
        Sequence = sequence;
        SentMicros = sentMicros;
        Frame = frame;
        Previous = previous;
    }

    public uint Sequence { get; }

    /// <summary>On the sender's clock: only differences between packets mean anything.</summary>
    public long SentMicros { get; }

    public ReadOnlySpan<byte> Frame { get; }

    /// <summary>The frame of <c>Sequence - 1</c>, or empty.</summary>
    public ReadOnlySpan<byte> Previous { get; }

    public int Write(Span<byte> destination)
    {
        int size = HeaderSize + 2 + Frame.Length + 2 + Previous.Length;
        if (destination.Length < size)
        {
            throw new ArgumentException("The packet does not fit.", nameof(destination));
        }

        destination[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], Sequence);
        BinaryPrimitives.WriteInt64BigEndian(destination[5..], SentMicros);
        int offset = HeaderSize;
        offset = WriteBlock(destination, offset, Frame);
        WriteBlock(destination, offset, Previous);
        return size;
    }

    public static bool TryRead(ReadOnlySpan<byte> data, out AudioPacket packet)
    {
        packet = default;
        if (data.Length < HeaderSize + 4 || data[0] != Version)
        {
            return false;
        }

        int offset = HeaderSize;
        ReadOnlySpan<byte> frame = ReadBlock(data, ref offset);
        ReadOnlySpan<byte> previous = ReadBlock(data, ref offset);
        if (offset < 0)
        {
            return false;
        }

        packet = new AudioPacket(BinaryPrimitives.ReadUInt32BigEndian(data[1..]), BinaryPrimitives.ReadInt64BigEndian(data[5..]), frame, previous);
        return true;
    }

    private static int WriteBlock(Span<byte> destination, int offset, ReadOnlySpan<byte> block)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)block.Length);
        block.CopyTo(destination[(offset + 2)..]);
        return offset + 2 + block.Length;
    }

    /// <summary>A length-prefixed block; sets <paramref name="offset"/> to -1 when it is cut short.</summary>
    private static ReadOnlySpan<byte> ReadBlock(ReadOnlySpan<byte> data, scoped ref int offset)
    {
        if (offset < 0 || data.Length < offset + 2)
        {
            offset = -1;
            return default;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        if (data.Length < offset + 2 + length)
        {
            offset = -1;
            return default;
        }

        ReadOnlySpan<byte> block = data.Slice(offset + 2, length);
        offset += 2 + length;
        return block;
    }
}
