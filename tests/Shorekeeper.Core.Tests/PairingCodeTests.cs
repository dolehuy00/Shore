using System.Net;
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

    [Fact]
    public void An_invitation_link_round_trips_and_rejects_what_is_not_one()
    {
        DeviceId id = DeviceId.FromSubjectPublicKeyInfo([7, 7, 7]);
        var link = new PairingInviteLink(id, [IPEndPoint.Parse("192.168.10.28:47471"), IPEndPoint.Parse("192.168.137.1:47471")], "aB-_9");

        string text = link.Format();

        Assert.True(PairingInviteLink.TryParse(text, out PairingInviteLink? read));
        Assert.Equal(id, read.DeviceId);
        Assert.Equal(link.Endpoints, read.Endpoints);
        Assert.Equal("aB-_9", read.Invite);
        Assert.DoesNotContain("aB-_9", link.ToString());
        Assert.False(PairingInviteLink.TryParse("https://example.com/pair?id=x", out _));
        Assert.False(PairingInviteLink.TryParse(text.Replace(id.Value, "not-an-id"), out _));
        Assert.False(PairingInviteLink.TryParse($"shorekeeper://pair?id={id.Value}&a=&k=x", out _));
        Assert.False(PairingInviteLink.TryParse($"shorekeeper://pair?id={id.Value}&a=10.0.0.1:1", out _));
    }
}
