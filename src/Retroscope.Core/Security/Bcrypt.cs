using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using BCrypt.Net;

namespace Retroscope.Core.Security;

/// <summary>
/// bcrypt for every password (DESIGN §e): imported GoTrue <c>$2a$</c>/<c>$2b$</c> cost-10 hashes verify unchanged,
/// new hashes are <c>$2a$10$</c>. Passwords are compared as their raw UTF-8 bytes; no normalization, no policy.
/// </summary>
public static partial class Bcrypt {
    public const int WorkFactor = 10;
    public const int MaxPasswordBytes = 72;

    // Cost-10 hash of a discarded random secret: unknown accounts pay the same verify cost as real ones.
    private const string DummyHash = "$2a$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi";

    /// <exception cref="ArgumentException">The password is longer than <see cref="MaxPasswordBytes"/> UTF-8 bytes.</exception>
    public static string Hash(string password) {
        ArgumentNullException.ThrowIfNull(password);
        if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes) {
            throw new ArgumentException($"bcrypt uses at most {MaxPasswordBytes} bytes of a password.", nameof(password));
        }

        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public static bool Verify(string password, [NotNullWhen(true)] string? hash) {
        ArgumentNullException.ThrowIfNull(password);
        if (!IsWellFormed(hash)) {
            return false;
        }

        try {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        } catch (Exception e) when (e is SaltParseException or ArgumentException) {
            return false;
        }
    }

    /// <summary>Spends the same time as a real verify; always false. Use when the account or its hash is missing.</summary>
    public static bool DummyVerify(string password) {
        Verify(password, DummyHash);
        return false;
    }

    /// <summary>The <c>password_hash</c> column constraint.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? hash) => hash is not null && HashShape().IsMatch(hash);

    [GeneratedRegex(@"^\$2[aby]\$[0-9]{2}\$[./A-Za-z0-9]{53}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashShape();
}
