using System.Buffers.Text;
using System.Security.Cryptography;

namespace Retroscope.Core.Security;

/// <summary>32 CSPRNG bytes as unpadded base64url: the session cookie, magic/reset/verify and invite tokens.</summary>
public static class OpaqueToken {
    public const int ByteLength = 32;
    public const int EncodedLength = 43;

    public static string New() {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        var token = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return token;
    }

    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == EncodedLength
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
