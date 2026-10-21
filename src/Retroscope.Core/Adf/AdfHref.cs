using Retroscope.Core.Markup;

namespace Retroscope.Core.Adf;

internal static class AdfHref {
    // The markup link token cannot hold these characters, and the percent-encoded URL means the same thing.
    public static string Normalize(string href) {
        return href.Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("<", "%3C", StringComparison.Ordinal)
            .Replace(">", "%3E", StringComparison.Ordinal);
    }

    // http, https or mailto only (§f); anything else renders as plain text.
    public static string? Allowed(string? href) {
        if (href == null) {
            return null;
        }
        var normalized = Normalize(href);
        return MarkupSyntax.IsAllowedHref(normalized) ? normalized : null;
    }
}
