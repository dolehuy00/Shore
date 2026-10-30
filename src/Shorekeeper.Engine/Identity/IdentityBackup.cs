using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Identity;

/// <summary>What a backup holds, shown before restoring it.</summary>
public sealed record BackupSummary(DeviceId DeviceId, string DisplayName, DateTimeOffset CreatedAt, int Contacts, int Groups);

/// <summary>A backup that was opened with the right password.</summary>
public sealed class OpenedBackup
{
    internal OpenedBackup(BackupPayload payload, BackupSummary summary)
    {
        Payload = payload;
        Summary = summary;
    }

    public BackupSummary Summary { get; }

    internal BackupPayload Payload { get; }
}

/// <summary>
/// Backup of the device identity with contacts and groups (docs/04-identity-security.md §1.1), so a new PC or a
/// reinstalled Windows keeps the same DeviceId and nobody has to Connect again.
/// File: "SKBK", version, PBKDF2 iterations, salt, AES-GCM nonce and tag, then the encrypted JSON.
/// A restore is staged and applied at the next start, before anything uses the identity or the stores.
/// </summary>
public sealed class IdentityBackup(
    AppPaths paths,
    IdentityStore identityStore,
    TrustStore trust,
    GroupStore groups,
    ISecretProtector protector,
    TimeProvider timeProvider,
    ILogger<IdentityBackup> logger)
{
    public const string FileExtension = ".skbackup";
    public const int MinPasswordLength = 8;

    private const uint Magic = 0x4B424B53; // "SKBK" little-endian
    private const byte FormatVersion = 1;
    private const int Iterations = 600_000;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 4 + 1 + 4 + SaltLength + NonceLength + TagLength;

    /// <summary>Writes a backup of the current identity, contacts (and blocked peers) and groups.</summary>
    public async Task ExportAsync(string path, string password, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(password.Length, MinPasswordLength, nameof(password));

        byte[] pkcs12 = identityStore.ReadPkcs12();
        BackupPayload payload;
        try
        {
            using X509Certificate2 certificate = LoadForInspection(pkcs12);
            payload = new BackupPayload(
                FormatVersion,
                timeProvider.GetUtcNow(),
                DeviceIdentity.FromCertificate(certificate).Value,
                displayName,
                Convert.ToBase64String(pkcs12),
                [.. trust.GetAll(TrustLevel.Trusted).Concat(trust.GetAll(TrustLevel.Blocked)).Select(BackupContact.From)],
                [.. groups.GetAll().Select(BackupGroup.From)],
                // Contacts carry theirs; these are for the others (members of a group we are not connected with).
                [.. trust.GetAliases().Where(a => trust.Get(a.Key) is null).Select(a => new BackupAlias(a.Key.Value, a.Value))]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        byte[] file = Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload, BackupJsonContext.Default.BackupPayload), password);
        string temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, file, cancellationToken);
        File.Move(temp, path, overwrite: true);
        logger.LogInformation("Identity backup written: {Contacts} contacts, {Groups} groups", payload.Contacts.Count, payload.Groups.Count);
    }

    /// <exception cref="InvalidDataException">Not a Shorekeeper backup, or a damaged one.</exception>
    /// <exception cref="CryptographicException">Wrong password, or the file was changed.</exception>
    public static OpenedBackup Open(byte[] file, string password)
    {
        BackupPayload payload = JsonSerializer.Deserialize(Decrypt(file, password), BackupJsonContext.Default.BackupPayload)
            ?? throw new InvalidDataException("Backup is empty.");
        if (payload.Version != FormatVersion || !DeviceId.TryParse(payload.DeviceId, out DeviceId id))
        {
            throw new InvalidDataException("Backup has an unknown format.");
        }

        byte[] pkcs12 = Convert.FromBase64String(payload.Identity);
        try
        {
            using X509Certificate2 certificate = LoadForInspection(pkcs12);
            if (!certificate.HasPrivateKey || DeviceIdentity.FromCertificate(certificate) != id)
            {
                throw new InvalidDataException("Backup identity does not match its DeviceId.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return new OpenedBackup(payload, new BackupSummary(id, payload.DisplayName, payload.CreatedAt, payload.Contacts.Count, payload.Groups.Count));
    }

    /// <summary>Keeps the backup (encrypted for this user only) until the next start applies it.</summary>
    public void StageRestore(OpenedBackup backup)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(backup.Payload, BackupJsonContext.Default.BackupPayload);
        try
        {
            File.WriteAllBytes(paths.PendingRestoreFile, protector.Protect(json));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>
    /// Applies a staged restore: the identity is replaced, and contacts and groups become those of the backup.
    /// Called by the engine after the stores are loaded and before the identity is.
    /// </summary>
    public async Task ApplyPendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        string path = paths.PendingRestoreFile;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            BackupPayload payload = JsonSerializer.Deserialize(protector.Unprotect(await File.ReadAllBytesAsync(path, cancellationToken)), BackupJsonContext.Default.BackupPayload)
                ?? throw new InvalidDataException("Pending restore is empty.");

            foreach (PeerRecord record in trust.GetAll(TrustLevel.Trusted).Concat(trust.GetAll(TrustLevel.Blocked)))
            {
                await trust.ForgetAsync(record.DeviceId, cancellationToken);
            }

            // Forgetting keeps a name we gave someone; the names of the identity being replaced go too.
            foreach (DeviceId named in trust.GetAliases().Keys)
            {
                await trust.SetAliasAsync(named, null, cancellationToken);
            }

            foreach (Group group in groups.GetAll())
            {
                await groups.RemoveAsync(group.Id, cancellationToken);
            }

            foreach (BackupContact contact in payload.Contacts)
            {
                if (!DeviceId.TryParse(contact.DeviceId, out DeviceId id))
                {
                    continue;
                }

                if (contact.Blocked)
                {
                    await trust.BlockAsync(id, contact.DisplayName, contact.HostName, cancellationToken);
                    continue;
                }

                await trust.SetTrustedAsync(id, contact.DisplayName, contact.HostName, cancellationToken);
                await trust.SetAliasAsync(id, contact.Alias, cancellationToken);
                await trust.SetAutoAcceptAsync(id, contact.AutoAcceptMaxBytes, cancellationToken);
            }

            foreach (BackupGroup group in payload.Groups)
            {
                if (group.ToGroup() is { } restored)
                {
                    await groups.SaveAsync(restored, cancellationToken);
                }
            }

            // Backups made before these were kept have none.
            foreach (BackupAlias alias in payload.Aliases ?? [])
            {
                if (DeviceId.TryParse(alias.DeviceId, out DeviceId id))
                {
                    await trust.SetAliasAsync(id, alias.Alias, cancellationToken);
                }
            }

            byte[] pkcs12 = Convert.FromBase64String(payload.Identity);
            try
            {
                identityStore.Replace(pkcs12);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }

            File.Delete(path);
            logger.LogInformation("Identity {DeviceId} restored with {Contacts} contacts and {Groups} groups",
                payload.DeviceId, payload.Contacts.Count, payload.Groups.Count);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException or FormatException)
        {
            // Never retry a broken restore on every start; keep it for diagnosis.
            File.Move(path, $"{path}.failed-{timeProvider.GetUtcNow():yyyyMMddHHmmss}");
            logger.LogError(ex, "Staged identity restore could not be applied");
        }
    }

    private static byte[] Encrypt(byte[] plaintext, string password)
    {
        byte[] file = new byte[HeaderLength + plaintext.Length];
        Span<byte> header = file.AsSpan(0, HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], Iterations);
        Span<byte> salt = header.Slice(9, SaltLength);
        Span<byte> nonce = header.Slice(9 + SaltLength, NonceLength);
        Span<byte> tag = header.Slice(9 + SaltLength + NonceLength, TagLength);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        byte[] key = DeriveKey(password, salt, Iterations);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            // The parameters before the tag are authenticated too.
            aes.Encrypt(nonce, plaintext, file.AsSpan(HeaderLength), tag, header[..(9 + SaltLength + NonceLength)]);
            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Decrypt(byte[] file, string password)
    {
        if (file.Length <= HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic || file[4] != FormatVersion)
        {
            throw new InvalidDataException("Not a Shorekeeper backup.");
        }

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(5));
        if (iterations is < 100_000 or > 10_000_000)
        {
            throw new InvalidDataException("Backup has invalid parameters.");
        }

        ReadOnlySpan<byte> header = file.AsSpan(0, HeaderLength);
        byte[] key = DeriveKey(password, header.Slice(9, SaltLength), iterations);
        byte[] plaintext = new byte[file.Length - HeaderLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                header.Slice(9 + SaltLength, NonceLength),
                file.AsSpan(HeaderLength),
                header.Slice(9 + SaltLength + NonceLength, TagLength),
                plaintext,
                header[..(9 + SaltLength + NonceLength)]);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Only to read the DeviceId: the key is not written to any key store (macOS has no ephemeral keys).</summary>
    private static X509Certificate2 LoadForInspection(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, password: null, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
}

internal sealed record BackupPayload(
    int Version,
    DateTimeOffset CreatedAt,
    string DeviceId,
    string DisplayName,
    string Identity,
    IReadOnlyList<BackupContact> Contacts,
    IReadOnlyList<BackupGroup> Groups,
    IReadOnlyList<BackupAlias>? Aliases = null);

/// <summary>"Tên gợi nhớ" of someone who is not a contact (a group member we are not connected with).</summary>
internal sealed record BackupAlias(string DeviceId, string Alias);

internal sealed record BackupContact(string DeviceId, string DisplayName, string HostName, string? Alias, long? AutoAcceptMaxBytes, bool Blocked)
{
    public static BackupContact From(PeerRecord record) => new(
        record.DeviceId.Value, record.DisplayName, record.HostName, record.Alias, record.AutoAcceptMaxBytes, record.TrustLevel == TrustLevel.Blocked);
}

internal sealed record BackupMember(string DeviceId, string Name, bool IsHost);

/// <param name="Role">"host", "member" or "pending".</param>
internal sealed record BackupGroup(string Id, string Name, string HostId, string Role, IReadOnlyList<BackupMember> Members)
{
    public static BackupGroup From(Group group) => new(
        group.Id,
        group.Name,
        group.HostId.Value,
        group.Role switch
        {
            GroupRole.Host => "host",
            GroupRole.Pending => "pending",
            _ => "member",
        },
        [.. group.Members.Select(m => new BackupMember(m.DeviceId.Value, m.Name, m.IsHost))]);

    public Group? ToGroup()
    {
        if (!DeviceId.TryParse(HostId, out DeviceId host))
        {
            return null;
        }

        GroupRole role = Role switch
        {
            "host" => GroupRole.Host,
            "pending" => GroupRole.Pending,
            _ => GroupRole.Member,
        };
        List<GroupMember> members = [];
        foreach (BackupMember member in Members)
        {
            if (DeviceId.TryParse(member.DeviceId, out DeviceId id))
            {
                members.Add(new GroupMember(id, member.Name, member.IsHost));
            }
        }

        return new Group(Id, Name, host, role, members);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupPayload))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
