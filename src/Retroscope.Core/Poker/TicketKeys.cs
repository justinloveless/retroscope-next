using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Retroscope.Core.Poker;

/// <summary>Jira issue keys as <c>poker_rounds.ticket_key</c> and <c>parent_key</c> accept them.</summary>
public static partial class TicketKeys {
    private const int MaxInputLength = 64;

    public static bool IsValid([NotNullWhen(true)] string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// Trims and upper-cases; a bare issue number gets <paramref name="prefix"/>. Empty input means "no ticket"
    /// (a placeholder round) and gives true with a null key.
    /// </summary>
    public static bool TryNormalize(string? input, string? prefix, out string? key) {
        key = null;
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed)) {
            return true;
        }
        if (trimmed.Length > MaxInputLength) {
            return false;
        }
        var candidate = trimmed.ToUpperInvariant();
        if (NumberPattern().IsMatch(candidate)) {
            if (prefix is null) {
                return false;
            }
            candidate = $"{prefix}-{candidate}";
        }
        if (!IsValid(candidate)) {
            return false;
        }
        key = candidate;
        return true;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,8}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[1-9][0-9]{0,8}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
