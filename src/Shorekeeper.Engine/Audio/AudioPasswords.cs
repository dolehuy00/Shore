using System.Security.Cryptography;
using System.Text.Json;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Audio;

/// <summary>
/// Passwords that worked for sources we listened to, so "Nghe" asks only the first time (and after the source changes
/// it). One small file, protected as a whole by <see cref="ISecretProtector"/> (DPAPI on Windows, Keystore on Android).
/// </summary>
public sealed class AudioPasswords(AppPaths paths, ISecretProtector protector)
{
    private readonly Lock gate = new();
    private Dictionary<string, string>? passwords;

    private string FilePath => Path.Combine(paths.DataDirectory, "audio-passwords.bin");

    public string? Get(DeviceId source)
    {
        lock (gate)
        {
            return Load().GetValueOrDefault(source.Value);
        }
    }

    public void Set(DeviceId source, string password)
    {
        lock (gate)
        {
            Load()[source.Value] = password;
            Save();
        }
    }

    public void Remove(DeviceId source)
    {
        lock (gate)
        {
            if (Load().Remove(source.Value))
            {
                Save();
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (passwords is not null)
        {
            return passwords;
        }

        try
        {
            passwords = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(File.ReadAllBytes(FilePath))) ?? []
                : [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Another machine's file (a restored backup) or a damaged one: asking again is all that is lost.
            passwords = [];
        }

        return passwords;
    }

    private void Save() => File.WriteAllBytes(FilePath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(passwords)));
}
