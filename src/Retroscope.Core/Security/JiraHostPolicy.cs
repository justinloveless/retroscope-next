using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Retroscope.Core.Security;

/// <summary>
/// Normalizes and vets a team's Jira <c>site_host</c> (DESIGN §f): strip scheme, path, port and trailing slash,
/// lower-case, then require <c>&lt;name&gt;.atlassian.net</c> or an exact <c>JIRA_HOST_ALLOWLIST</c> entry.
/// Userinfo, percent-encoding, backslashes, non-ASCII and IP literals in any notation are refused.
/// </summary>
public sealed partial class JiraHostPolicy {
    public const int MaxHostLength = 253;

    private readonly HashSet<string> _allowlist;

    /// <exception cref="ArgumentException">An allow-list entry is not a plain DNS host name.</exception>
    public JiraHostPolicy(IEnumerable<string> allowlist) {
        ArgumentNullException.ThrowIfNull(allowlist);
        _allowlist = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in allowlist) {
            if (!TryNormalizeShape(entry, out var host) || !HostName().IsMatch(host)) {
                throw new ArgumentException($"JIRA_HOST_ALLOWLIST entry '{entry}' is not a DNS host name.", nameof(allowlist));
            }

            _allowlist.Add(host);
        }
    }

    public static JiraHostPolicy AtlassianCloudOnly { get; } = new([]);

    /// <summary>Parses the comma-separated <c>JIRA_HOST_ALLOWLIST</c> value; empty means Atlassian Cloud only.</summary>
    public static JiraHostPolicy FromAllowlist(string? commaSeparated) =>
        new((commaSeparated ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public bool TryNormalize(string? input, [NotNullWhen(true)] out string? host) {
        if (TryNormalizeShape(input, out var candidate)
            && (AtlassianCloud().IsMatch(candidate) || _allowlist.Contains(candidate))) {
            host = candidate;
            return true;
        }

        host = null;
        return false;
    }

    private static bool TryNormalizeShape(string? input, [NotNullWhen(true)] out string? host) {
        host = null;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value)) {
            return false;
        }

        foreach (var c in value) {
            if (c <= ' ' || c >= '\x7f' || c is '@' or '%' or '\\' or '[' or ']') {
                return false;
            }
        }

        value = value.ToLowerInvariant();
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) {
            var scheme = value[..schemeEnd];
            if (scheme is not ("https" or "http")) {
                return false;
            }

            value = value[(schemeEnd + 3)..];
        }

        var authorityEnd = value.IndexOfAny(['/', '?', '#']);
        if (authorityEnd >= 0) {
            value = value[..authorityEnd];
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0) {
            var port = value[(colon + 1)..];
            if (port.Length is 0 or > 5 || !port.All(char.IsAsciiDigit)) {
                return false;
            }

            value = value[..colon];
        }

        if (value.Length is 0 or > MaxHostLength || IsIpLiteral(value)) {
            return false;
        }

        host = value;
        return true;
    }

    // WHATWG URL parsing treats a host whose last label is numeric (decimal, 0x-hex, octal) as IPv4,
    // so such names are refused even when IPAddress would not parse the whole string.
    private static bool IsIpLiteral(string host) {
        if (IPAddress.TryParse(host, out _)) {
            return true;
        }

        var lastLabel = host.TrimEnd('.');
        lastLabel = lastLabel[(lastLabel.LastIndexOf('.') + 1)..];
        return lastLabel.Length > 0
            && (lastLabel.All(char.IsAsciiDigit)
                || (lastLabel.StartsWith("0x", StringComparison.Ordinal) && lastLabel[2..].All(char.IsAsciiHexDigit)));
    }

    [GeneratedRegex(@"^[a-z0-9-]+\.atlassian\.net\z", RegexOptions.CultureInvariant)]
    private static partial Regex AtlassianCloud();

    [GeneratedRegex(@"^(?=.{1,253}\z)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
