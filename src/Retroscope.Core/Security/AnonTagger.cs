using System.Security.Cryptography;

namespace Retroscope.Core.Security;

/// <summary>Proves ownership of anonymous cards and comments without storing the author (DESIGN §c.2, <c>anon_tag</c>).</summary>
public interface IAnonTagger {
    byte[] Tag(Guid boardId, Guid principalId);

    bool Owns(ReadOnlySpan<byte> tag, Guid boardId, Guid principalId);
}

/// <summary>HMAC-SHA256(<c>ANON_TAG_KEY</c>, board ‖ principal), each id as its 16 RFC 4122 (big-endian) bytes.</summary>
public sealed class AnonTagger : IAnonTagger {
    public const int KeySize = 32;
    public const int TagSize = 32;

    private readonly byte[] _key;

    public AnonTagger(ReadOnlySpan<byte> key) {
        if (key.Length != KeySize) {
            throw new ArgumentException($"ANON_TAG_KEY must be {KeySize} bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>Parses <c>ANON_TAG_KEY</c> (base64 of 32 bytes). Errors never echo the key.</summary>
    public static AnonTagger Parse(string anonTagKey) {
        ArgumentNullException.ThrowIfNull(anonTagKey);
        Span<byte> key = stackalloc byte[KeySize];
        try {
            if (!Convert.TryFromBase64String(anonTagKey.Trim(), key, out var written) || written != KeySize) {
                throw new FormatException($"ANON_TAG_KEY is not base64 of exactly {KeySize} bytes.");
            }

            return new AnonTagger(key);
        } finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Tag(Guid boardId, Guid principalId) {
        Span<byte> message = stackalloc byte[32];
        boardId.TryWriteBytes(message[..16], bigEndian: true, out _);
        principalId.TryWriteBytes(message[16..], bigEndian: true, out _);
        return HMACSHA256.HashData(_key, message);
    }

    public bool Owns(ReadOnlySpan<byte> tag, Guid boardId, Guid principalId) =>
        tag.Length == TagSize && CryptographicOperations.FixedTimeEquals(Tag(boardId, principalId), tag);
}
