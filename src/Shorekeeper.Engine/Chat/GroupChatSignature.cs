using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Chat;

/// <summary>What a group message's signature covers (docs/16-group-chat.md §3.5).</summary>
internal readonly record struct GroupChatContent(
    string GroupId, string Id, DeviceId Author, long SentAtMs, string Kind, string? Text, int DurationMs, byte[]? Wave, byte[]? Audio);

/// <summary>
/// Group messages and their recalls are signed with the author's device key (docs/16-group-chat.md §3.5), so a member
/// passing one on can neither forge nor change it. The public key travels with the message: its hash must be the
/// author's DeviceId, so nobody needs to have met the author before.
/// </summary>
internal static class GroupChatSignature
{
    private const string MessagePurpose = "shorekeeper-group-chat/1";
    private const string RecallPurpose = "shorekeeper-group-recall/1";

    public static byte[] SignMessage(ECDsa key, GroupChatContent content) =>
        key.SignData(MessageBytes(content), HashAlgorithmName.SHA256);

    public static bool VerifyMessage(byte[] authorKey, byte[] sig, GroupChatContent content) =>
        Verify(authorKey, sig, content.Author, MessageBytes(content));

    /// <summary>Signs taking a message back; its time is in it, so whoever passes the recall on cannot move it.</summary>
    public static byte[] SignRecall(ECDsa key, string groupId, string id, DeviceId author, long sentAtMs) =>
        key.SignData(RecallBytes(groupId, id, author, sentAtMs), HashAlgorithmName.SHA256);

    public static bool VerifyRecall(byte[] authorKey, byte[] sig, string groupId, string id, DeviceId author, long sentAtMs) =>
        Verify(authorKey, sig, author, RecallBytes(groupId, id, author, sentAtMs));

    private static bool Verify(byte[] authorKey, byte[] sig, DeviceId author, byte[] data)
    {
        if (DeviceId.FromSubjectPublicKeyInfo(authorKey) != author)
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(authorKey, out _);
            return key.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            // Not an EC key at all.
            return false;
        }
    }

    /// <summary>Each field after its length, so two fields can never be read as one.</summary>
    private static byte[] MessageBytes(GroupChatContent c)
    {
        using var stream = new MemoryStream();
        Write(stream, MessagePurpose);
        Write(stream, c.GroupId);
        Write(stream, c.Id);
        Write(stream, c.Author.Value);
        Write(stream, c.SentAtMs);
        Write(stream, c.Kind);
        Write(stream, c.Text ?? "");
        Write(stream, (long)c.DurationMs);
        Write(stream, c.Wave ?? []);
        Write(stream, c.Audio is { } audio ? SHA256.HashData(audio) : []);
        return stream.ToArray();
    }

    private static byte[] RecallBytes(string groupId, string id, DeviceId author, long sentAtMs)
    {
        using var stream = new MemoryStream();
        Write(stream, RecallPurpose);
        Write(stream, groupId);
        Write(stream, id);
        Write(stream, author.Value);
        Write(stream, sentAtMs);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value) => Write(stream, Encoding.UTF8.GetBytes(value));

    private static void Write(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Write(stream, bytes);
    }

    private static void Write(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
