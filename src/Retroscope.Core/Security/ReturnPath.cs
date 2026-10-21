using System.Diagnostics.CodeAnalysis;

namespace Retroscope.Core.Security;

/// <summary>
/// Validates the OIDC <c>return</c> parameter (DESIGN §e, S14): only a same-origin path. The value and every
/// percent-decoded form of it must be free of control characters, whitespace and backslashes and must not start with
/// <c>//</c>, so neither a browser nor a later decoding step can turn it into a network-path reference.
/// </summary>
public static class ReturnPath {
    public const int MaxLength = 2048;
    private const int MaxDecodingRounds = 3;

    public static bool TryValidate(Uri publicUrl, string? value, [NotNullWhen(true)] out string? path) {
        ArgumentNullException.ThrowIfNull(publicUrl);
        path = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !IsSafeForm(value)) {
            return false;
        }

        var decoded = value;
        for (var round = 0; ; round++) {
            var next = Uri.UnescapeDataString(decoded);
            if (next == decoded) {
                break;
            }
            if (round == MaxDecodingRounds || !IsSafeForm(next)) {
                return false;
            }

            decoded = next;
        }

        if (!Uri.TryCreate(publicUrl, value, out var resolved)
            || Uri.Compare(resolved, publicUrl, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0
            || !IsSafeForm(resolved.AbsolutePath)) {
            return false;
        }

        path = resolved.PathAndQuery + resolved.Fragment;
        return true;
    }

    private static bool IsSafeForm(string candidate) {
        if (candidate.Length == 0 || candidate[0] != '/' || candidate.StartsWith("//", StringComparison.Ordinal)) {
            return false;
        }

        foreach (var c in candidate) {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\') {
                return false;
            }
        }

        return true;
    }
}
