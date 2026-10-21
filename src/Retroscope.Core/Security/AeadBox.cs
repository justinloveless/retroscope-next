using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Retroscope.Core.Security;

/// <summary>AES-256-GCM with associated data. Layout: nonce (12) ‖ ciphertext ‖ tag (16).</summary>
public static class AeadBox {
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData) {
        var box = new byte[plaintext.Length + Overhead];
        Seal(key, plaintext, associatedData, box);
        return box;
    }

    public static void Seal(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        Span<byte> destination) {
        RequireKey(key);
        if (destination.Length != plaintext.Length + Overhead) {
            throw new ArgumentException("Destination must be exactly plaintext length + Overhead.", nameof(destination));
        }

        var nonce = destination[..NonceSize];
        var ciphertext = destination.Slice(NonceSize, plaintext.Length);
        var tag = destination[(NonceSize + plaintext.Length)..];
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    public static int PlaintextLength(ReadOnlySpan<byte> box) => Math.Max(box.Length - Overhead, -1);

    public static bool TryOpen(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> box,
        ReadOnlySpan<byte> associatedData,
        [NotNullWhen(true)] out byte[]? plaintext) {
        plaintext = null;
        if (box.Length < Overhead) {
            return false;
        }

        var buffer = new byte[box.Length - Overhead];
        if (!TryOpen(key, box, associatedData, buffer)) {
            return false;
        }

        plaintext = buffer;
        return true;
    }

    /// <summary>Decrypts into <paramref name="destination"/>, which must be exactly <see cref="PlaintextLength"/> long. Cleared on failure.</summary>
    public static bool TryOpen(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> box,
        ReadOnlySpan<byte> associatedData,
        Span<byte> destination) {
        RequireKey(key);
        if (box.Length < Overhead || destination.Length != box.Length - Overhead) {
            return false;
        }

        var nonce = box[..NonceSize];
        var ciphertext = box.Slice(NonceSize, destination.Length);
        var tag = box[(NonceSize + destination.Length)..];
        try {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, destination, associatedData);
            return true;
        } catch (CryptographicException) {
            CryptographicOperations.ZeroMemory(destination);
            return false;
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key) {
        if (key.Length != KeySize) {
            throw new ArgumentException($"AES-256-GCM needs a {KeySize}-byte key.", nameof(key));
        }
    }
}
