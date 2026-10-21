using System.Security.Cryptography;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class TokenTests {
    [Fact]
    public void Opaque_tokens_are_43_base64url_characters_and_unique() {
        var tokens = Enumerable.Range(0, 1000).Select(_ => OpaqueToken.New()).ToList();

        Assert.All(tokens, t => {
            Assert.Equal(OpaqueToken.EncodedLength, t.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", t);
            Assert.True(OpaqueToken.IsWellFormed(t));
        });
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Malformed_opaque_tokens_are_refused(string? token) {
        Assert.False(OpaqueToken.IsWellFormed(token));
    }

    [Fact]
    public void Token_hash_is_sha256_of_the_utf8_token() {
        // FIPS 180-2 appendix B.1: SHA-256("abc").
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Convert.ToHexStringLower(TokenHash.Of("abc")));
    }

    [Fact]
    public void Token_hash_matches_only_its_token() {
        var token = OpaqueToken.New();
        var stored = TokenHash.Of(token);

        Assert.Equal(TokenHash.Length, stored.Length);
        Assert.True(TokenHash.Matches(token, stored));
        Assert.False(TokenHash.Matches(OpaqueToken.New(), stored));
        Assert.False(TokenHash.Matches(token, stored.AsSpan(0, 31)));
    }

    [Fact]
    public void Anon_tag_is_hmac_of_board_then_principal_in_rfc_4122_byte_order() {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var board = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var principal = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
        var message = Convert.FromHexString("00112233445566778899aabbccddeeff" + "ffeeddccbbaa99887766554433221100");

        var tag = AnonTagger.Parse(Convert.ToBase64String(key)).Tag(board, principal);

        Assert.Equal(HMACSHA256.HashData(key, message), tag);
    }

    [Fact]
    public void Anon_tag_proves_ownership_only_for_the_same_board_and_principal() {
        var tagger = new AnonTagger(RandomNumberGenerator.GetBytes(AnonTagger.KeySize));
        var board = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var tag = tagger.Tag(board, principal);

        Assert.True(tagger.Owns(tag, board, principal));
        Assert.False(tagger.Owns(tag, Guid.NewGuid(), principal));
        Assert.False(tagger.Owns(tag, board, Guid.NewGuid()));
        Assert.False(tagger.Owns(tag, principal, board));
        Assert.False(tagger.Owns(tag.AsSpan(1), board, principal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64")]
    public void Malformed_anon_tag_keys_are_refused(string key) {
        Assert.Throws<FormatException>(() => AnonTagger.Parse(key));
    }
}
