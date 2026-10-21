using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Retroscope.Core.Ids;

/// <summary>
/// The shareable code in a room URL. New codes are 10 Crockford base32 characters (S9). Imported codes are
/// kept verbatim (6-character team codes, 13-character lower-case board codes, UUID text), so parsing only
/// checks the shared grammar <c>[A-Za-z0-9-]{3,64}</c> and never rewrites characters. Codes compare
/// <see cref="LookupKey"/>, matching the <c>upper(code)</c> indexes.
/// </summary>
public readonly record struct RoomCode {
    public const int GeneratedLength = 10;
    public const int MinLength = 3;
    public const int MaxLength = 64;
    public const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private RoomCode(string value) {
        Value = value;
    }

    public string Value { get; }

    public string LookupKey => Value.ToUpperInvariant();

    public static RoomCode NewRandom() {
        Span<byte> random = stackalloc byte[GeneratedLength];
        RandomNumberGenerator.Fill(random);
        return FromRandomBytes(random);
    }

    /// <summary>Maps each byte to one Crockford character; 256 is a multiple of 32, so there is no bias.</summary>
    public static RoomCode FromRandomBytes(ReadOnlySpan<byte> random) {
        if (random.Length != GeneratedLength) {
            throw new ArgumentException($"Expected {GeneratedLength} random bytes.", nameof(random));
        }
        Span<char> chars = stackalloc char[GeneratedLength];
        for (var i = 0; i < GeneratedLength; i++) {
            chars[i] = CrockfordAlphabet[random[i] & 31];
        }
        return new RoomCode(new string(chars));
    }

    public static bool TryParse([NotNullWhen(true)] string? text, out RoomCode code) {
        code = default;
        if (text is null || text.Length is < MinLength or > MaxLength) {
            return false;
        }
        foreach (var c in text) {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') {
                return false;
            }
        }
        code = new RoomCode(text);
        return true;
    }

    public bool Equals(RoomCode other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value ?? "";
}
