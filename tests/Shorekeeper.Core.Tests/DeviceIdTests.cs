using System.Security.Cryptography;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Core.Tests;

public class DeviceIdTests
{
    [Fact]
    public void FromSubjectPublicKeyInfo_is_deterministic_and_52_chars()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();

        DeviceId first = DeviceId.FromSubjectPublicKeyInfo(spki);
        DeviceId second = DeviceId.FromSubjectPublicKeyInfo(spki);

        Assert.Equal(first, second);
        Assert.Equal(DeviceId.Length, first.Value.Length);
        Assert.True(DeviceId.TryParse(first.Value, out _));
    }

    [Fact]
    public void Different_keys_give_different_ids()
    {
        using ECDsa a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa b = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.NotEqual(
            DeviceId.FromSubjectPublicKeyInfo(a.ExportSubjectPublicKeyInfo()),
            DeviceId.FromSubjectPublicKeyInfo(b.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void ShortForm_is_first_eight_chars_grouped()
    {
        DeviceId id = DeviceId.Parse(new string('a', 4) + new string('b', 4) + new string('c', 44));

        Assert.Equal("aaaa-bbbb", id.ShortForm);
    }

    [Fact]
    public void TryParse_normalizes_case_and_whitespace()
    {
        string value = new('k', 52);

        Assert.True(DeviceId.TryParse($"  {value.ToUpperInvariant()} ", out DeviceId id));
        Assert.Equal(value, id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1")] // invalid digit
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 53 chars
    public void TryParse_rejects_invalid_values(string? text)
    {
        Assert.False(DeviceId.TryParse(text, out DeviceId id));
        Assert.True(id.IsEmpty);
    }
}
