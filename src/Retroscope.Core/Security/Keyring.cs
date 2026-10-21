using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Retroscope.Core.Security;

/// <summary>
/// The <c>APP_KEYS</c> keyring (DESIGN §e). The first key seals; every key opens. Per-purpose subkeys are
/// HKDF-SHA256(master, salt: none, info: <c>retroscope/&lt;purpose&gt;/v1</c>).
/// A box is <c>version (1) ‖ idLength (1) ‖ keyId ‖ AeadBox</c>; the header is authenticated with the caller's
/// associated data, so a box names the key that opens it and the id cannot be swapped.
/// </summary>
public sealed class Keyring {
    public const int MasterKeySize = 32;
    public const int MaxKeyIdLength = 32;
    private const byte BoxVersion = 1;

    private readonly Dictionary<(string KeyId, KeyPurpose Purpose), byte[]> _subkeys;

    private Keyring(string activeKeyId, IReadOnlyList<string> keyIds, Dictionary<(string, KeyPurpose), byte[]> subkeys) {
        ActiveKeyId = activeKeyId;
        KeyIds = keyIds;
        _subkeys = subkeys;
    }

    public string ActiveKeyId { get; }

    public IReadOnlyList<string> KeyIds { get; }

    /// <summary>Parses <c>"k3:&lt;base64 32 bytes&gt;,k2:&lt;base64 32 bytes&gt;"</c>. Errors never echo key material.</summary>
    public static Keyring Parse(string appKeys) {
        ArgumentNullException.ThrowIfNull(appKeys);
        var ids = new List<string>();
        var subkeys = new Dictionary<(string, KeyPurpose), byte[]>();
        foreach (var rawEntry in appKeys.Split(',')) {
            var entry = rawEntry.Trim();
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) {
                throw new FormatException($"APP_KEYS entry {ids.Count + 1} is not '<id>:<base64>'.");
            }

            var id = entry[..colon];
            if (!IsValidKeyId(id)) {
                throw new FormatException(
                    $"APP_KEYS entry {ids.Count + 1} has an invalid id (1-{MaxKeyIdLength} of A-Z a-z 0-9 _ -).");
            }
            if (ids.Contains(id)) {
                throw new FormatException($"APP_KEYS id '{id}' appears twice.");
            }

            var master = DecodeMasterKey(entry[(colon + 1)..], id);
            try {
                foreach (var purpose in Enum.GetValues<KeyPurpose>()) {
                    subkeys[(id, purpose)] = Derive(master, [], Encoding.ASCII.GetBytes(KeyPurposeInfo.Of(purpose)), 32);
                }
            } finally {
                CryptographicOperations.ZeroMemory(master);
            }
            ids.Add(id);
        }

        return new Keyring(ids[0], ids.AsReadOnly(), subkeys);
    }

    public byte[] Seal(KeyPurpose purpose, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData) {
        var headerLength = 2 + ActiveKeyId.Length;
        var box = new byte[headerLength + plaintext.Length + AeadBox.Overhead];
        box[0] = BoxVersion;
        box[1] = (byte)ActiveKeyId.Length;
        Encoding.ASCII.GetBytes(ActiveKeyId, box.AsSpan(2));

        var header = box.AsSpan(0, headerLength);
        var aad = BindHeader(header, associatedData);
        AeadBox.Seal(_subkeys[(ActiveKeyId, purpose)], plaintext, aad, box.AsSpan(headerLength));
        return box;
    }

    public bool TryOpen(
        KeyPurpose purpose,
        ReadOnlySpan<byte> box,
        ReadOnlySpan<byte> associatedData,
        [NotNullWhen(true)] out byte[]? plaintext) {
        plaintext = null;
        var length = PlaintextLength(box);
        if (length < 0) {
            return false;
        }

        var buffer = new byte[length];
        if (!TryOpen(purpose, box, associatedData, buffer)) {
            return false;
        }

        plaintext = buffer;
        return true;
    }

    /// <summary>Decrypts into a caller-owned (e.g. pooled) buffer of exactly <see cref="PlaintextLength"/> bytes.</summary>
    public bool TryOpen(KeyPurpose purpose, ReadOnlySpan<byte> box, ReadOnlySpan<byte> associatedData, Span<byte> destination) {
        if (!TryReadKeyId(box, out var keyId) || !_subkeys.TryGetValue((keyId, purpose), out var key)) {
            return false;
        }

        var headerLength = 2 + keyId.Length;
        var aad = BindHeader(box[..headerLength], associatedData);
        return AeadBox.TryOpen(key, box[headerLength..], aad, destination);
    }

    /// <summary>Re-encrypts a box under the active key (the rotation command). False if the box does not open.</summary>
    public bool TryReseal(
        KeyPurpose purpose,
        ReadOnlySpan<byte> box,
        ReadOnlySpan<byte> associatedData,
        [NotNullWhen(true)] out byte[]? resealed) {
        resealed = null;
        if (!TryOpen(purpose, box, associatedData, out var plaintext)) {
            return false;
        }

        try {
            resealed = Seal(purpose, plaintext, associatedData);
            return true;
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool IsSealedWithActiveKey(ReadOnlySpan<byte> box) => TryReadKeyId(box, out var keyId) && keyId == ActiveKeyId;

    /// <summary>HMAC-SHA256 under the active key's subkey (e.g. <c>audit_log.meta.email_hmac</c>).</summary>
    public byte[] Mac(KeyPurpose purpose, ReadOnlySpan<byte> data) => HMACSHA256.HashData(_subkeys[(ActiveKeyId, purpose)], data);

    /// <summary>Plaintext length of a well-formed box, or -1.</summary>
    public static int PlaintextLength(ReadOnlySpan<byte> box) {
        if (!TryReadKeyId(box, out var keyId)) {
            return -1;
        }

        var length = box.Length - 2 - keyId.Length - AeadBox.Overhead;
        return length < 0 ? -1 : length;
    }

    public static bool TryReadKeyId(ReadOnlySpan<byte> box, [NotNullWhen(true)] out string? keyId) {
        keyId = null;
        if (box.Length < 2 || box[0] != BoxVersion || box[1] == 0 || box[1] > MaxKeyIdLength || box.Length < 2 + box[1]) {
            return false;
        }

        var candidate = Encoding.ASCII.GetString(box.Slice(2, box[1]));
        if (!IsValidKeyId(candidate)) {
            return false;
        }

        keyId = candidate;
        return true;
    }

    internal static byte[] Derive(ReadOnlySpan<byte> inputKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length) {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, output, salt, info);
        return output;
    }

    private static byte[] BindHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> associatedData) {
        var aad = new byte[header.Length + associatedData.Length];
        header.CopyTo(aad);
        associatedData.CopyTo(aad.AsSpan(header.Length));
        return aad;
    }

    private static byte[] DecodeMasterKey(string base64, string id) {
        var buffer = new byte[MasterKeySize];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written) || written != MasterKeySize) {
            CryptographicOperations.ZeroMemory(buffer);
            throw new FormatException($"APP_KEYS key '{id}' is not base64 of exactly {MasterKeySize} bytes.");
        }

        return buffer;
    }

    private static bool IsValidKeyId(string id) {
        if (id.Length is 0 or > MaxKeyIdLength) {
            return false;
        }

        foreach (var c in id) {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') {
                return false;
            }
        }

        return true;
    }
}
