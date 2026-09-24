using System.Security.Cryptography;

namespace Shorekeeper.Platform.Windows.Tests;

public class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector protector = new();

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(1024);

        byte[] protectedData = protector.Protect(secret);

        Assert.NotEqual(secret, protectedData);
        Assert.Equal(secret, protector.Unprotect(protectedData));
    }

    [Fact]
    public void Tampered_data_is_rejected()
    {
        byte[] protectedData = protector.Protect([1, 2, 3, 4]);
        protectedData[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedData));
    }
}
