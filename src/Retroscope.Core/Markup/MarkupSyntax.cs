namespace Retroscope.Core.Markup;

// Character classes are ASCII-only on purpose: web/src/markup/parse.js must classify every UTF-16 unit the same way.
internal static class MarkupSyntax {
    public const int MaxBlockDepth = 16;
    public const int MaxInlineDepth = 16;
    public const int MaxMentionNameLength = 100;
    public const int MaxImageAltLength = 100;
    public const int MaxHrefLength = 2048;
    public const int MaxLanguageLength = 32;
    public const int MaxIslandIdLength = 64;

    public const string MentionPrefix = "[[mention:";
    public const string ImagePrefix = "](blob:";
    public const string IslandPrefix = "[[island:";

    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n';

    public static bool IsAsciiAlphanumeric(char c) => char.IsAsciiLetterOrDigit(c);

    public static bool IsAsciiPunctuation(char c) {
        return c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    }

    public static bool IsBlank(string line) {
        foreach (var c in line) {
            if (c is not (' ' or '\t')) {
                return false;
            }
        }
        return true;
    }

    // Canonical 8-4-4-4-12 hex only, so both parsers accept exactly the same tokens.
    public static bool TryParseUuid(string text, int start, out Guid id) {
        id = Guid.Empty;
        if (start + 36 > text.Length) {
            return false;
        }
        for (var i = 0; i < 36; i++) {
            var c = text[start + i];
            var valid = i is 8 or 13 or 18 or 23 ? c == '-' : char.IsAsciiHexDigit(c);
            if (!valid) {
                return false;
            }
        }
        id = Guid.ParseExact(text.AsSpan(start, 36), "D");
        return true;
    }

    public static bool IsAllowedHref(string href) {
        if (href.Length is 0 or > MaxHrefLength) {
            return false;
        }
        foreach (var c in href) {
            if (!IsHrefChar(c)) {
                return false;
            }
        }
        return HasSchemeAndRest(href, "http://") || HasSchemeAndRest(href, "https://") || HasSchemeAndRest(href, "mailto:");
    }

    public static bool IsHrefChar(char c) => c > ' ' && c != '\u007f' && c is not ('(' or ')' or '<' or '>');

    public static bool IsIslandId(ReadOnlySpan<char> id) {
        if (id.Length is 0 or > MaxIslandIdLength) {
            return false;
        }
        foreach (var c in id) {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) {
                return false;
            }
        }
        return true;
    }

    public static bool IsLanguage(ReadOnlySpan<char> language) {
        if (language.Length is 0 or > MaxLanguageLength) {
            return false;
        }
        foreach (var c in language) {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '#' or '.' or '-')) {
                return false;
            }
        }
        return true;
    }

    public readonly record struct ListMarker(bool Ordered, int Start, int Width, string Content);

    public static bool TryListMarker(string line, out ListMarker marker) {
        marker = default;
        if (line.Length == 0) {
            return false;
        }
        if (line[0] == '-') {
            if (line.Length > 1 && line[1] != ' ') {
                return false;
            }
            marker = new ListMarker(false, 0, 2, line.Length > 2 ? line[2..] : "");
            return true;
        }
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) {
            digits++;
        }
        if (digits is 0 or > 9 || digits == line.Length || line[digits] != '.') {
            return false;
        }
        var width = digits + 2;
        if (line.Length > digits + 1 && line[digits + 1] != ' ') {
            return false;
        }
        var start = int.Parse(line.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture);
        marker = new ListMarker(true, start, width, line.Length > width ? line[width..] : "");
        return true;
    }

    public static bool TryHeading(string line, out int level, out string content) {
        level = CountLeading(line, '#');
        content = "";
        if (level is 0 or > 6 || line.Length <= level || line[level] != ' ') {
            return false;
        }
        content = line[(level + 1)..].Trim(' ', '\t');
        return content.Length > 0;
    }

    public static bool IsRule(string line) {
        var trimmed = line.AsSpan().TrimEnd(" \t");
        return trimmed.Length >= 3 && !trimmed.ContainsAnyExcept('-');
    }

    public static int CountLeading(string line, char c) {
        var count = 0;
        while (count < line.Length && line[count] == c) {
            count++;
        }
        return count;
    }

    private static bool HasSchemeAndRest(string href, string scheme) {
        return href.Length > scheme.Length && href.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);
    }
}
