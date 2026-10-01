using System.Security.Cryptography;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;

namespace Shorekeeper.Core.Tests;

public class PairingCodeTests
{
    private static readonly DeviceId A = DeviceId.FromSubjectPublicKeyInfo([1, 2, 3]);
    private static readonly DeviceId B = DeviceId.FromSubjectPublicKeyInfo([4, 5, 6]);
    private static readonly DeviceId Mallory = DeviceId.FromSubjectPublicKeyInfo([7, 8, 9]);

    private readonly byte[] nonceA = RandomNumberGenerator.GetBytes(PairingCode.NonceSize);
    private readonly byte[] nonceB = RandomNumberGenerator.GetBytes(PairingCode.NonceSize);

    [Fact]
    public void Both_sides_compute_the_same_code()
    {
        Assert.Equal(PairingCode.Compute(A, B, nonceA, nonceB), PairingCode.Compute(B, A, nonceA, nonceB));
    }

    [Fact]
    public void Code_is_six_digits_grouped()
    {
        Assert.Matches(@"^\d{3} \d{3}$", PairingCode.Compute(A, B, nonceA, nonceB));
    }

    [Fact]
    public void Man_in_the_middle_produces_a_different_code()
    {
        // A talks to Mallory thinking it is B; B talks to Mallory thinking it is A.
        string seenByA = PairingCode.Compute(A, Mallory, nonceA, nonceB);
        string seenByB = PairingCode.Compute(Mallory, B, nonceA, nonceB);

        Assert.NotEqual(seenByA, seenByB);
    }

    [Fact]
    public void Different_nonces_give_different_codes()
    {
        byte[] otherNonce = RandomNumberGenerator.GetBytes(PairingCode.NonceSize);

        Assert.NotEqual(PairingCode.Compute(A, B, nonceA, nonceB), PairingCode.Compute(A, B, otherNonce, nonceB));
    }
}
