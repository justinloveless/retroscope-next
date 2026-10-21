using System.Security.Cryptography;
using System.Text;

namespace Retroscope.Core.Ids;

/// <summary>Name-based UUIDs (RFC 9562 §5.5, SHA-1) for synthetic imported identities.</summary>
public static class UuidV5 {
    public static Guid Create(Guid namespaceId, string name) {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 9562 fixes SHA-1 for v5; the output is an identifier, not a security boundary.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
