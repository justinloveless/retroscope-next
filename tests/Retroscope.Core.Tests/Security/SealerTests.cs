using System.Security.Cryptography;
using System.Text;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class SealerTests : IClassFixture<SealerTests.TestRsaKey> {
    private readonly TestRsaKey _rsa;

    public SealerTests(TestRsaKey rsa) {
        _rsa = rsa;
    }

    [Fact]
    public void Sealed_value_round_trips_with_the_private_key() {
        using var sealer = Sealer.Create(_rsa.PublicPem);

        var sealedValue = sealer.Seal("auth.users", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "recovery_token", "tok-123");

        Assert.True(Sealer.TryOpen(
            _rsa.Key, sealer.WrappedKey.Span, "auth.users", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "recovery_token",
            sealedValue, out var value));
        Assert.Equal("tok-123", Encoding.UTF8.GetString(value));
    }

    [Theory]
    [InlineData("auth.identities", "row-1", "recovery_token")]
    [InlineData("auth.users", "row-2", "recovery_token")]
    [InlineData("auth.users", "row-1", "phone")]
    [InlineData("auth.users", "row-1\0phone", "recovery_token")]
    public void Sealed_value_is_bound_to_table_primary_key_and_field(string table, string primaryKey, string field) {
        using var sealer = Sealer.Create(_rsa.PublicPem);
        var sealedValue = sealer.Seal("auth.users", "row-1", "recovery_token", "tok");

        if (primaryKey.Contains('\0', StringComparison.Ordinal)) {
            Assert.Throws<ArgumentException>(() =>
                Sealer.TryOpen(_rsa.Key, sealer.WrappedKey.Span, table, primaryKey, field, sealedValue, out _));
        } else {
            Assert.False(Sealer.TryOpen(_rsa.Key, sealer.WrappedKey.Span, table, primaryKey, field, sealedValue, out _));
        }
    }

    [Fact]
    public void Another_private_key_cannot_unwrap_the_run_key() {
        using var sealer = Sealer.Create(_rsa.PublicPem);
        var sealedValue = sealer.Seal("teams", "t1", "jira_api_key", "secret");
        using var other = RSA.Create(Sealer.MinimumRsaKeySize);

        Assert.False(Sealer.TryOpen(other, sealer.WrappedKey.Span, "teams", "t1", "jira_api_key", sealedValue, out _));
    }

    [Fact]
    public void Each_run_has_its_own_key_but_the_same_fingerprint() {
        using var first = Sealer.Create(_rsa.PublicPem);
        using var second = Sealer.Create(_rsa.PublicPem);
        var sealedValue = first.Seal("teams", "t1", "jira_api_key", "secret");

        Assert.False(Sealer.TryOpen(_rsa.Key, second.WrappedKey.Span, "teams", "t1", "jira_api_key", sealedValue, out _));
        Assert.Equal(first.KeyFingerprint, second.KeyFingerprint);
        Assert.Equal(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_rsa.Key.ExportSubjectPublicKeyInfo())),
            first.KeyFingerprint);
    }

    [Fact]
    public void Garbage_sealed_values_do_not_open() {
        using var sealer = Sealer.Create(_rsa.PublicPem);

        Assert.False(Sealer.TryOpen(_rsa.Key, sealer.WrappedKey.Span, "t", "p", "f", "not base64!", out _));
        Assert.False(Sealer.TryOpen(_rsa.Key, sealer.WrappedKey.Span, "t", "p", "f", "AAAA", out _));
        Assert.False(Sealer.TryOpen(_rsa.Key, new byte[384], "t", "p", "f", "AAAA", out _));
    }

    [Fact]
    public void A_null_sealed_value_does_not_open() {
        using var sealer = Sealer.Create(_rsa.PublicPem);

        Assert.False(Sealer.TryOpen(_rsa.Key, sealer.WrappedKey.Span, "t", "p", "f", null, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Keys_shorter_than_rsa_3072_are_refused() {
        using var small = RSA.Create(2048);

        Assert.Throws<ArgumentException>(() => Sealer.Create(small.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Sealing_after_dispose_throws() {
        var sealer = Sealer.Create(_rsa.PublicPem);
        sealer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sealer.Seal("t", "p", "f", "v"));
    }

    public sealed class TestRsaKey : IDisposable {
        public TestRsaKey() {
            Key = RSA.Create(Sealer.MinimumRsaKeySize);
            PublicPem = Key.ExportSubjectPublicKeyInfoPem();
        }

        public RSA Key { get; }

        public string PublicPem { get; }

        public void Dispose() => Key.Dispose();
    }
}
