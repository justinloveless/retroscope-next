using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Retroscope.Core.Security;

/// <summary>
/// Importer sealing (DESIGN §h.3): one random AES-256-GCM key per run, wrapped with RSA-OAEP-SHA256 under the offline
/// public key. A sealed value is base64(AeadBox(K, value, AAD = table ‖ 0x00 ‖ pk ‖ 0x00 ‖ field)). Only the holder
/// of the private key can open it.
/// </summary>
public sealed class Sealer : IDisposable {
    public const int MinimumRsaKeySize = 3072;

    private readonly byte[] _key;
    private bool _disposed;

    private Sealer(byte[] key, byte[] wrappedKey, string keyFingerprint) {
        _key = key;
        WrappedKey = wrappedKey;
        KeyFingerprint = keyFingerprint;
    }

    /// <summary>The run key, RSA-OAEP-SHA256 wrapped; stored in the run report.</summary>
    public ReadOnlyMemory<byte> WrappedKey { get; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the public key's SubjectPublicKeyInfo (<c>import_runs.seal_key_id</c>).</summary>
    public string KeyFingerprint { get; }

    public static Sealer Create(string publicKeyPem) {
        using var rsa = ImportRsa(publicKeyPem);
        var key = RandomNumberGenerator.GetBytes(AeadBox.KeySize);
        var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        return new Sealer(key, wrapped, Fingerprint(rsa));
    }

    public string Seal(string table, string primaryKey, string field, string value) =>
        Seal(table, primaryKey, field, Encoding.UTF8.GetBytes(value));

    public string Seal(string table, string primaryKey, string field, ReadOnlySpan<byte> value) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToBase64String(AeadBox.Seal(_key, value, AssociatedData(table, primaryKey, field)));
    }

    /// <summary>Operator-side recovery with the offline private key.</summary>
    public static bool TryOpen(
        RSA privateKey,
        ReadOnlySpan<byte> wrappedKey,
        string table,
        string primaryKey,
        string field,
        string sealedValue,
        [NotNullWhen(true)] out byte[]? value) {
        ArgumentNullException.ThrowIfNull(privateKey);
        value = null;
        byte[] key;
        try {
            key = privateKey.Decrypt(wrappedKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
        } catch (CryptographicException) {
            return false;
        }

        try {
            var box = new byte[(sealedValue.Length + 3) / 4 * 3];
            return key.Length == AeadBox.KeySize
                && Convert.TryFromBase64String(sealedValue, box, out var written)
                && AeadBox.TryOpen(key, box.AsSpan(0, written), AssociatedData(table, primaryKey, field), out value);
        } finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string Fingerprint(RSA rsa) {
        ArgumentNullException.ThrowIfNull(rsa);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }

    public void Dispose() {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private static RSA ImportRsa(string publicKeyPem) {
        var rsa = RSA.Create();
        try {
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < MinimumRsaKeySize) {
                throw new ArgumentException(
                    $"The seal key must be RSA-{MinimumRsaKeySize} or larger, got RSA-{rsa.KeySize}.",
                    nameof(publicKeyPem));
            }

            return rsa;
        } catch {
            rsa.Dispose();
            throw;
        }
    }

    private static byte[] AssociatedData(string table, string primaryKey, string field) {
        foreach (var part in (ReadOnlySpan<string>)[table, primaryKey, field]) {
            ArgumentNullException.ThrowIfNull(part);
            if (part.Contains('\0', StringComparison.Ordinal)) {
                throw new ArgumentException("Seal coordinates cannot contain NUL.");
            }
        }

        return Encoding.UTF8.GetBytes($"{table}\0{primaryKey}\0{field}");
    }
}
