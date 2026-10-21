using System.Security.Cryptography;
using System.Text;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class KeyringTests {
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("jira:v1:2f1b0c7e-0000-4000-8000-000000000001");
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void First_key_is_active_and_every_key_is_listed() {
        var keyring = Keyring.Parse($"k3:{KeyB}, k2:{KeyA}");

        Assert.Equal("k3", keyring.ActiveKeyId);
        Assert.Equal(["k3", "k2"], keyring.KeyIds);
    }

    [Fact]
    public void Box_round_trips_and_names_its_key() {
        var keyring = Keyring.Parse($"k2:{KeyA}");
        var plaintext = Encoding.UTF8.GetBytes("""{"email":"a@b.c","token":"secret"}""");

        var box = keyring.Seal(KeyPurpose.JiraCredentials, plaintext, Aad);

        Assert.True(Keyring.TryReadKeyId(box, out var keyId));
        Assert.Equal("k2", keyId);
        Assert.True(keyring.TryOpen(KeyPurpose.JiraCredentials, box, Aad, out var opened));
        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void After_rotation_old_boxes_still_open_and_reseal_under_the_new_key() {
        var before = Keyring.Parse($"k2:{KeyA}");
        var oldBox = before.Seal(KeyPurpose.InviteBox, "invite-token"u8, Aad);
        var after = Keyring.Parse($"k3:{KeyB},k2:{KeyA}");

        Assert.False(after.IsSealedWithActiveKey(oldBox));
        Assert.True(after.TryOpen(KeyPurpose.InviteBox, oldBox, Aad, out var opened));
        Assert.Equal("invite-token"u8.ToArray(), opened);

        Assert.True(after.TryReseal(KeyPurpose.InviteBox, oldBox, Aad, out var resealed));
        Assert.True(after.IsSealedWithActiveKey(resealed));
        Assert.True(after.TryOpen(KeyPurpose.InviteBox, resealed, Aad, out var reopened));
        Assert.Equal("invite-token"u8.ToArray(), reopened);
    }

    [Fact]
    public void Box_under_a_retired_key_does_not_open() {
        var box = Keyring.Parse($"k2:{KeyA}").Seal(KeyPurpose.InviteBox, "x"u8, Aad);

        Assert.False(Keyring.Parse($"k3:{KeyB}").TryOpen(KeyPurpose.InviteBox, box, Aad, out _));
    }

    [Fact]
    public void Same_key_id_with_different_material_does_not_open() {
        var box = Keyring.Parse($"k2:{KeyA}").Seal(KeyPurpose.InviteBox, "x"u8, Aad);

        Assert.False(Keyring.Parse($"k2:{KeyB}").TryOpen(KeyPurpose.InviteBox, box, Aad, out _));
    }

    [Fact]
    public void Tampered_associated_data_fails() {
        var keyring = Keyring.Parse($"k2:{KeyA}");
        var box = keyring.Seal(KeyPurpose.JiraCredentials, "x"u8, Aad);
        var otherTeam = Encoding.UTF8.GetBytes("jira:v1:2f1b0c7e-0000-4000-8000-000000000002");

        Assert.False(keyring.TryOpen(KeyPurpose.JiraCredentials, box, otherTeam, out _));
        Assert.False(keyring.TryOpen(KeyPurpose.JiraCredentials, box, [], out _));
    }

    [Fact]
    public void Box_sealed_for_one_purpose_does_not_open_for_another() {
        var keyring = Keyring.Parse($"k2:{KeyA}");
        var box = keyring.Seal(KeyPurpose.InviteBox, "x"u8, Aad);

        Assert.False(keyring.TryOpen(KeyPurpose.JiraCredentials, box, Aad, out _));
    }

    [Fact]
    public void Every_flipped_bit_is_detected() {
        var keyring = Keyring.Parse($"k2:{KeyA},k3:{KeyB}");
        var box = keyring.Seal(KeyPurpose.OidcCookie, "state-nonce-verifier"u8, Aad);

        for (var bit = 0; bit < box.Length * 8; bit++) {
            var tampered = (byte[])box.Clone();
            tampered[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(keyring.TryOpen(KeyPurpose.OidcCookie, tampered, Aad, out _), $"bit {bit}");
        }
    }

    [Fact]
    public void Truncated_or_garbage_boxes_are_refused() {
        var keyring = Keyring.Parse($"k2:{KeyA}");
        var box = keyring.Seal(KeyPurpose.OidcCookie, "x"u8, Aad);

        for (var length = 0; length < box.Length; length++) {
            Assert.False(keyring.TryOpen(KeyPurpose.OidcCookie, box.AsSpan(0, length), Aad, out byte[]? _));
        }
        Assert.False(keyring.TryOpen(KeyPurpose.OidcCookie, new byte[64], Aad, out byte[]? _));
        Assert.Equal(-1, Keyring.PlaintextLength([1, 2, (byte)'k', (byte)'2']));
    }

    [Fact]
    public void Opens_into_a_caller_buffer_and_clears_it_on_failure() {
        var keyring = Keyring.Parse($"k2:{KeyA}");
        var box = keyring.Seal(KeyPurpose.JiraCredentials, "token"u8, Aad);
        var buffer = new byte[Keyring.PlaintextLength(box)];

        Assert.True(keyring.TryOpen(KeyPurpose.JiraCredentials, box, Aad, buffer.AsSpan()));
        Assert.Equal("token"u8.ToArray(), buffer);

        Assert.False(keyring.TryOpen(KeyPurpose.JiraCredentials, box, "other"u8, buffer.AsSpan()));
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Each_seal_uses_a_fresh_nonce() {
        var keyring = Keyring.Parse($"k2:{KeyA}");

        Assert.NotEqual(
            keyring.Seal(KeyPurpose.InviteBox, "same"u8, Aad),
            keyring.Seal(KeyPurpose.InviteBox, "same"u8, Aad));
    }

    [Fact]
    public void Subkeys_are_hkdf_sha256_of_the_master_key_with_the_purpose_info() {
        var master = Convert.FromBase64String(KeyA);
        var expected = HMACSHA256.HashData(
            Keyring.Derive(master, [], Encoding.ASCII.GetBytes("retroscope/audit-email/v1"), 32),
            "j@example.com"u8);

        Assert.Equal(expected, Keyring.Parse($"k2:{KeyA}").Mac(KeyPurpose.AuditEmail, "j@example.com"u8));
    }

    [Fact]
    public void Mac_differs_per_purpose() {
        var keyring = Keyring.Parse($"k2:{KeyA}");

        Assert.NotEqual(keyring.Mac(KeyPurpose.AuditEmail, "x"u8), keyring.Mac(KeyPurpose.OidcCookie, "x"u8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("k2")]
    [InlineData(":AAAA")]
    [InlineData("k:2:AAAA")]
    [InlineData("k2:not-base64!")]
    [InlineData("k2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("k2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("a-very-long-key-identifier-over-32-chars:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("k2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=,")]
    public void Malformed_app_keys_are_refused(string appKeys) {
        Assert.Throws<FormatException>(() => Keyring.Parse(appKeys));
    }

    [Fact]
    public void Duplicate_key_ids_are_refused() {
        Assert.Throws<FormatException>(() => Keyring.Parse($"k2:{KeyA},k2:{KeyB}"));
    }

    [Fact]
    public void Parse_errors_do_not_echo_key_material() {
        var almostKey = KeyA[..^4] + "!!!!";

        var error = Assert.Throws<FormatException>(() => Keyring.Parse($"k2:{almostKey}"));

        Assert.DoesNotContain(almostKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("k2", error.Message, StringComparison.Ordinal);
    }
}
