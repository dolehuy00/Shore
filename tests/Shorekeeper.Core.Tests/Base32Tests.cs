using Shorekeeper.Core.Encoding;

namespace Shorekeeper.Core.Tests;

public class Base32Tests
{
    // RFC 4648 §10 test vectors, lower-cased and without padding.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "my")]
    [InlineData("fo", "mzxq")]
    [InlineData("foo", "mzxw6")]
    [InlineData("foob", "mzxw6yq")]
    [InlineData("fooba", "mzxw6ytb")]
    [InlineData("foobar", "mzxw6ytboi")]
    public void Encode_matches_rfc4648_vectors(string input, string expected)
    {
        Assert.Equal(expected, Base32.Encode(System.Text.Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Encode_of_sha256_has_52_characters()
    {
        Assert.Equal(52, Base32.Encode(new byte[32]).Length);
    }

    [Theory]
    [InlineData("abcxyz234567", true)]
    [InlineData("ABC", false)]
    [InlineData("a1", false)]
    [InlineData("a8", false)]
    [InlineData("a-b", false)]
    public void IsValid_accepts_only_alphabet(string text, bool expected)
    {
        Assert.Equal(expected, Base32.IsValid(text));
    }
}
