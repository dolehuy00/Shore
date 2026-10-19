using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Shorekeeper.Core.Audio;

/// <summary>
/// Seals the audio stream with AES-GCM under a key the listener makes for each session and hands over the mutual-TLS
/// API. Audio packets keep their header readable (and authenticated) and seal the frames; the listener's keep-alives
/// carry nothing but proof of the key (one sealed byte: Android's AES-GCM aborts the process on empty input). One
/// instance per thread: it reuses a buffer.
/// </summary>
public sealed class AudioCipher : IDisposable
{
    public const int KeySize = 32;
    public const int TagSize = 16;

    /// <summary>Size of a keep-alive: marker, counter, one sealed byte, tag.</summary>
    public const int KeepAliveSize = 6 + TagSize;

    private const byte KeepAliveMarker = 0xA1;
    private const byte AudioNonce = 0;
    private const byte KeepAliveNonce = 1;

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
    public int SealKeepAlive(Span<byte> destination, uint counter)
    {
        destination[0] = KeepAliveMarker;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], counter);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, KeepAliveNonce, destination[1..5]);
        aes.Encrypt(nonce, [0], destination.Slice(5, 1), destination.Slice(6, TagSize), destination[..5]);
        return KeepAliveSize;
    }

    public bool OpenKeepAlive(ReadOnlySpan<byte> datagram)
    {
        if (!IsKeepAlive(datagram))
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, KeepAliveNonce, datagram[1..5]);
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

    public void Dispose() => aes.Dispose();

    // Kind and counter: audio and keep-alives never share a nonce under the session's key.
    private static void Nonce(Span<byte> nonce, byte kind, ReadOnlySpan<byte> counter)
    {
        nonce.Clear();
        nonce[0] = kind;
        counter.CopyTo(nonce[4..]);
    }
}
