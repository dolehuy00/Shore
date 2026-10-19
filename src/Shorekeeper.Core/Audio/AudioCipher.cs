using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Shorekeeper.Core.Audio;

/// <summary>
/// Seals the audio stream with AES-GCM under a key the listener makes for each session and hands over the mutual-TLS
/// API. Audio packets keep their header readable (and authenticated) and seal the frames; the listener's keep-alives
/// carry nothing but proof of the key (one sealed byte: Android's AES-GCM aborts the process on empty input), the
/// source answers each with the same counter so the listener can time the round trip, and the listener's resend
/// requests carry the sequence numbers of the packets it missed. One instance per thread: it reuses a buffer.
/// </summary>
public sealed class AudioCipher : IDisposable
{
    public const int KeySize = 32;
    public const int TagSize = 16;

    /// <summary>Size of a keep-alive: marker, counter, one sealed byte, tag.</summary>
    public const int KeepAliveSize = 6 + TagSize;

    /// <summary>The most sequence numbers one resend request carries.</summary>
    public const int MaxResendSequences = 64;

    /// <summary>Size of the largest resend request: marker, counter, sequences, tag.</summary>
    public const int MaxResendSize = 5 + (4 * MaxResendSequences) + TagSize;

    private const byte KeepAliveMarker = 0xA1;
    private const byte ResendMarker = 0xA2;
    private const byte KeepAliveReplyMarker = 0xA3;
    private const byte AudioNonce = 0;
    private const byte KeepAliveNonce = 1;
    private const byte ResendNonce = 2;
    private const byte KeepAliveReplyNonce = 3;

    private readonly AesGcm aes;
    private readonly byte[] scratch = new byte[AudioPacket.MaxSize];

    public AudioCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("An audio key is 32 bytes.", nameof(key));
        }

        aes = new AesGcm(key, TagSize);
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static bool IsKeepAlive(ReadOnlySpan<byte> datagram) => datagram.Length == KeepAliveSize && datagram[0] == KeepAliveMarker;

    /// <summary>Seals a packet written by <see cref="AudioPacket.Write"/> in place; the buffer needs <see cref="TagSize"/> spare bytes.</summary>
    /// <returns>The sealed length.</returns>
    public int Seal(Span<byte> packet, int length)
    {
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, AudioNonce, packet[1..5]);
        Span<byte> body = scratch.AsSpan(0, length - AudioPacket.HeaderSize);
        aes.Encrypt(nonce, packet[AudioPacket.HeaderSize..length], body, packet.Slice(length, TagSize), packet[..AudioPacket.HeaderSize]);
        body.CopyTo(packet[AudioPacket.HeaderSize..]);
        return length + TagSize;
    }

    /// <summary>Opens a sealed packet in place.</summary>
    /// <returns>The packet's length without the tag, or -1 when it was not sealed with this key or was altered.</returns>
    public int Open(Span<byte> packet)
    {
        int length = packet.Length - TagSize;
        if (length < AudioPacket.HeaderSize)
        {
            return -1;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, AudioNonce, packet[1..5]);
        Span<byte> body = scratch.AsSpan(0, length - AudioPacket.HeaderSize);
        try
        {
            aes.Decrypt(nonce, packet[AudioPacket.HeaderSize..length], packet.Slice(length, TagSize), body, packet[..AudioPacket.HeaderSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return -1;
        }

        body.CopyTo(packet[AudioPacket.HeaderSize..]);
        return length;
    }

    /// <summary>Writes the listener's "still here" with a counter that never repeats within the session.</summary>
    public int SealKeepAlive(Span<byte> destination, uint counter) => SealMarked(destination, KeepAliveMarker, KeepAliveNonce, counter);

    public bool OpenKeepAlive(ReadOnlySpan<byte> datagram) => OpenMarked(datagram, KeepAliveMarker, KeepAliveNonce);

    /// <summary>The source's answer to keep-alive <paramref name="counter"/>: the listener times the round trip ("Ping").</summary>
    public int SealKeepAliveReply(Span<byte> destination, uint counter) =>
        SealMarked(destination, KeepAliveReplyMarker, KeepAliveReplyNonce, counter);

    /// <param name="counter">The counter of the keep-alive it answers.</param>
    public bool OpenKeepAliveReply(ReadOnlySpan<byte> datagram, out uint counter)
    {
        counter = datagram.Length == KeepAliveSize ? BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]) : 0;
        return OpenMarked(datagram, KeepAliveReplyMarker, KeepAliveReplyNonce);
    }

    /// <summary>Writes the listener's "send these again", with a counter of its own that never repeats within the session.</summary>
    public int SealResendRequest(Span<byte> destination, uint counter, ReadOnlySpan<uint> sequences)
    {
        if (sequences.Length is 0 or > MaxResendSequences)
        {
            throw new ArgumentOutOfRangeException(nameof(sequences));
        }

        destination[0] = ResendMarker;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], counter);
        Span<byte> plain = stackalloc byte[4 * sequences.Length];
        for (int i = 0; i < sequences.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(plain[(4 * i)..], sequences[i]);
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, ResendNonce, destination[1..5]);
        aes.Encrypt(nonce, plain, destination.Slice(5, plain.Length), destination.Slice(5 + plain.Length, TagSize), destination[..5]);
        return 5 + plain.Length + TagSize;
    }

    /// <summary>Reads a resend request into <paramref name="sequences"/> (room for <see cref="MaxResendSequences"/>).</summary>
    /// <returns>How many sequence numbers it holds, or -1 when it is not one sealed with this key.</returns>
    public int OpenResendRequest(ReadOnlySpan<byte> datagram, Span<uint> sequences)
    {
        int bodyLength = datagram.Length - 5 - TagSize;
        if (datagram.Length == 0 || datagram[0] != ResendMarker || bodyLength <= 0 || bodyLength % 4 != 0 || bodyLength / 4 > MaxResendSequences)
        {
            return -1;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, ResendNonce, datagram[1..5]);
        Span<byte> plain = stackalloc byte[bodyLength];
        try
        {
            aes.Decrypt(nonce, datagram.Slice(5, bodyLength), datagram.Slice(5 + bodyLength, TagSize), plain, datagram[..5]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return -1;
        }

        int count = bodyLength / 4;
        for (int i = 0; i < count; i++)
        {
            sequences[i] = BinaryPrimitives.ReadUInt32BigEndian(plain[(4 * i)..]);
        }

        return count;
    }

    public void Dispose() => aes.Dispose();

    // A marker, a counter and one sealed byte: keep-alives and their replies.
    private int SealMarked(Span<byte> destination, byte marker, byte kind, uint counter)
    {
        destination[0] = marker;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], counter);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, kind, destination[1..5]);
        aes.Encrypt(nonce, [0], destination.Slice(5, 1), destination.Slice(6, TagSize), destination[..5]);
        return KeepAliveSize;
    }

    private bool OpenMarked(ReadOnlySpan<byte> datagram, byte marker, byte kind)
    {
        if (datagram.Length != KeepAliveSize || datagram[0] != marker)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, kind, datagram[1..5]);
        Span<byte> plain = stackalloc byte[1];
        try
        {
            aes.Decrypt(nonce, datagram.Slice(5, 1), datagram.Slice(6, TagSize), plain, datagram[..5]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    // Kind and counter: audio, keep-alives, their replies and resend requests never share a nonce under the session's key.
    private static void Nonce(Span<byte> nonce, byte kind, ReadOnlySpan<byte> counter)
    {
        nonce.Clear();
        nonce[0] = kind;
        counter.CopyTo(nonce[4..]);
    }
}
