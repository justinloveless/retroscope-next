using System.Security.Cryptography;
using FsCheck.Xunit;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class AeadBoxTests {
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(AeadBox.KeySize);

    [Property]
    public bool Round_trips_any_plaintext_and_associated_data(byte[] plaintext, byte[] associatedData) {
        var box = AeadBox.Seal(Key, plaintext, associatedData);

        return box.Length == plaintext.Length + AeadBox.Overhead
            && AeadBox.TryOpen(Key, box, associatedData, out var opened)
            && opened.AsSpan().SequenceEqual(plaintext);
    }

    [Fact]
    public void Tampered_associated_data_fails() {
        var box = AeadBox.Seal(Key, "value"u8, "users\0id-1\0phone"u8);

        Assert.False(AeadBox.TryOpen(Key, box, "users\0id-2\0phone"u8, out _));
        Assert.False(AeadBox.TryOpen(Key, box, "users\0id-1\0email"u8, out _));
    }

    [Fact]
    public void Tampered_ciphertext_or_tag_fails() {
        var box = AeadBox.Seal(Key, "value"u8, []);

        foreach (var index in new[] { 0, AeadBox.NonceSize, box.Length - 1 }) {
            var tampered = (byte[])box.Clone();
            tampered[index] ^= 0x01;
            Assert.False(AeadBox.TryOpen(Key, tampered, [], out _));
        }
    }

    [Fact]
    public void Wrong_key_fails() {
        var box = AeadBox.Seal(Key, "value"u8, []);

        Assert.False(AeadBox.TryOpen(RandomNumberGenerator.GetBytes(AeadBox.KeySize), box, [], out _));
    }

    [Fact]
    public void Short_keys_are_refused() {
        Assert.Throws<ArgumentException>(() => AeadBox.Seal(new byte[16], "x"u8, []));
    }
}
