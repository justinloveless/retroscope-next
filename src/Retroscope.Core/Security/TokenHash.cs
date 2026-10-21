using System.Security.Cryptography;
using System.Text;

namespace Retroscope.Core.Security;

/// <summary>
/// What the database stores for a bearer token: SHA-256 of the token string's UTF-8 bytes, so new base64url tokens
/// and imported legacy invite tokens hash the same way.
/// </summary>
public static class TokenHash {
    public const int Length = 32;

    public static byte[] Of(string token) {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public static bool Matches(string token, ReadOnlySpan<byte> storedHash) =>
        storedHash.Length == Length && CryptographicOperations.FixedTimeEquals(Of(token), storedHash);
}
