using System.Text;
using static Retroscope.Core.Markup.MarkupSyntax;

namespace Retroscope.Core.Markup;

// Parses one paragraph's text (lines joined by '\n'). Scopes are index ranges into the same string, so token
// lookups are cached by absolute position: a token found from an outer scope is the same token seen from an inner
// one, and an 8,000-character hostile input stays quadratic at worst.
internal sealed class InlineParser(string text, bool retro) {
    private enum Delimiter {
        Double,
        Star,
        Underscore,
    }

    private readonly record struct CodeToken(int End, bool IsSpan, int ContentStart, int ContentEnd);

    private readonly record struct LinkToken(int End, int LabelEnd, string Href);

    private readonly record struct AtomToken(int End, Guid Id, string Text);

    private readonly record struct CloserKey(Delimiter Delimiter, char Char, int End, bool InLink);

    private readonly Dictionary<int, CodeToken> _codes = [];
    private readonly Dictionary<int, LinkToken?> _links = [];
    private readonly Dictionary<int, AtomToken?> _mentions = [];
    private readonly Dictionary<int, AtomToken?> _images = [];
    private readonly Dictionary<CloserKey, Dictionary<int, int>> _closers = [];
    private Brackets? _brackets;

    // Delimiters already open around a scope. Emphasis never nests inside emphasis opened with the same
    // delimiter: the outer closer search would stop at the inner delimiter, so no writer could reproduce it.
    [Flags]
    private enum Open {
        None = 0,
        Bold = 1,
        Strike = 2,
        StarItalic = 4,
        UnderscoreItalic = 8,
    }

    private readonly record struct Scope(int Start, int End, bool InLink, int Depth, Open Open);

    public List<DocInline> Parse() => Parse(new Scope(0, text.Length, false, 0, Open.None));

    private List<DocInline> Parse(Scope scope) {
        var output = new List<DocInline>();
        var pending = new StringBuilder();
        var end = scope.End;
        var i = scope.Start;
        while (i < end) {
            var c = text[i];
            if (c == '\\' && i + 1 < end && IsAsciiPunctuation(text[i + 1])) {
                pending.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '\n') {
                Flush(output, pending);
                output.Add(new DocLineBreak());
                i++;
                continue;
            }
            if (c == '`') {
                var code = Code(i);
                if (code.IsSpan && code.End <= end) {
                    Flush(output, pending);
                    output.Add(new DocCode(text[code.ContentStart..code.ContentEnd]));
                    i = code.End;
                } else {
                    var runEnd = RunEnd(i, '`', end);
                    pending.Append(text, i, runEnd - i);
                    i = runEnd;
                }
                continue;
            }
            var next = i;
            var node = scope.InLink ? null : Atom(i, scope, out next);
            if (node == null && c is '*' or '_' or '~' && scope.Depth < MaxInlineDepth) {
                node = Emphasis(i, scope, out next);
            }
            if (node != null) {
                Flush(output, pending);
                output.Add(node);
                i = next;
                continue;
            }
            pending.Append(c);
            i++;
        }
        Flush(output, pending);
        return output;
    }

    private DocInline? Atom(int i, Scope scope, out int next) {
        next = i;
        if (Mention(i) is { } mention && mention.End <= scope.End) {
            next = mention.End;
            return new DocMention(mention.Id, mention.Text);
        }
        if (Image(i) is { } image && image.End <= scope.End) {
            next = image.End;
            return new DocImage(image.Id, image.Text);
        }
        if (Link(i) is { } link && link.End <= scope.End) {
            next = link.End;
            var label = new Scope(i + 1, link.LabelEnd, true, scope.Depth + 1, Open.None);
            return new DocLink(link.Href, Parse(label));
        }
        return null;
    }

    private DocStyled? Emphasis(int i, Scope scope, out int next) {
        next = i;
        var (start, end) = (scope.Start, scope.End);
        var c = text[i];
        var (style, delimiter, length) = c switch {
            '*' when Opens(i, 2, scope, Open.Bold) && text[i + 1] == '*' => (DocStyle.Bold, Delimiter.Double, 2),
            '~' when Opens(i, 2, scope, Open.Strike) && text[i + 1] == '~' => (DocStyle.Strike, Delimiter.Double, 2),
            '_' when Opens(i, 1, scope, Open.UnderscoreItalic) && (i == start || !IsAsciiAlphanumeric(text[i - 1]))
                => (DocStyle.Italic, Delimiter.Underscore, 1),
            _ => (DocStyle.Italic, Delimiter.Star, 0),
        };
        var closer = length > 0 ? FindCloser(c, delimiter, i + length, end, scope.InLink) : -1;
        if (closer < 0 && c == '*' && Opens(i, 1, scope, Open.StarItalic)) {
            (style, delimiter, length) = (DocStyle.Italic, Delimiter.Star, 1);
            closer = FindCloser(c, delimiter, i + 1, end, scope.InLink);
        }
        if (closer < 0) {
            return null;
        }
        var opened = style switch {
            DocStyle.Bold => Open.Bold,
            DocStyle.Strike => Open.Strike,
            _ => c == '*' ? Open.StarItalic : Open.UnderscoreItalic,
        };
        var inner = scope with { Start = i + length, End = closer, Depth = scope.Depth + 1, Open = scope.Open | opened };
        next = closer + length;
        return new DocStyled(style, Parse(inner));
    }

    private bool Opens(int i, int length, Scope scope, Open delimiter) {
        return (scope.Open & delimiter) == 0 && FollowedByContent(i, length, scope.End);
    }

    private bool FollowedByContent(int i, int length, int end) {
        return i + length < end && !IsWhitespace(text[i + length]);
    }

    // The first closer at or after contentStart. A scan from any later position k does not depend on where the
    // opener was (only on the delimiter, the scope end and whether links are tokens), so results from k onwards are
    // memoised: every failed opener in "**a **a **a…" would otherwise rescan to the end.
    private int FindCloser(char c, Delimiter delimiter, int contentStart, int end, bool inLink) {
        var (candidate, next) = CloserStep(c, delimiter, contentStart, end, inLink);
        if (candidate > contentStart) {
            return candidate;
        }
        var key = new CloserKey(delimiter, c, end, inLink);
        if (!_closers.TryGetValue(key, out var memo)) {
            memo = [];
            _closers[key] = memo;
        }
        var visited = new List<int>();
        var result = -1;
        for (var k = next; k < end;) {
            if (memo.TryGetValue(k, out result)) {
                break;
            }
            visited.Add(k);
            (candidate, k) = CloserStep(c, delimiter, k, end, inLink);
            result = candidate;
            if (candidate >= 0) {
                break;
            }
        }
        foreach (var k in visited) {
            memo[k] = result;
        }
        return result;
    }

    // Examines the token or delimiter run at k: a valid closer position (or -1) and where the scan continues.
    private (int Candidate, int Next) CloserStep(char c, Delimiter delimiter, int k, int end, bool inLink) {
        var skipped = SkipToken(k, end, inLink);
        if (skipped > k) {
            return (-1, skipped);
        }
        if (text[k] != c) {
            return (-1, k + 1);
        }
        var runEnd = RunEnd(k, c, end);
        var run = runEnd - k;
        var candidate = delimiter switch {
            Delimiter.Double => run >= 2 ? runEnd - 2 : -1,
            Delimiter.Star => run % 2 == 1 ? runEnd - 1 : -1,
            _ => run == 1 ? k : -1,
        };
        var valid = candidate > 0
            && !IsWhitespace(text[candidate - 1])
            && (delimiter != Delimiter.Underscore || candidate + 1 >= end || !IsAsciiAlphanumeric(text[candidate + 1]));
        return (valid ? candidate : -1, runEnd);
    }

    // Mirrors the token branches of Parse, so a closer is never found inside a code span, link, mention or image.
    private int SkipToken(int k, int end, bool inLink) {
        var c = text[k];
        if (c == '\\') {
            return k + 1 < end && IsAsciiPunctuation(text[k + 1]) ? k + 2 : k;
        }
        if (c == '`') {
            var code = Code(k);
            return code.IsSpan && code.End <= end ? code.End : RunEnd(k, '`', end);
        }
        if (inLink) {
            return k;
        }
        if (Mention(k) is { } mention && mention.End <= end) {
            return mention.End;
        }
        if (Image(k) is { } image && image.End <= end) {
            return image.End;
        }
        if (Link(k) is { } link && link.End <= end) {
            return link.End;
        }
        return k;
    }

    private CodeToken Code(int i) {
        if (_codes.TryGetValue(i, out var cached)) {
            return cached;
        }
        var runEnd = RunEnd(i, '`', text.Length);
        var length = runEnd - i;
        var token = new CodeToken(runEnd, false, 0, 0);
        var p = runEnd;
        while (p < text.Length && text[p] != '\n') {
            if (text[p] != '`') {
                p++;
                continue;
            }
            var closeEnd = RunEnd(p, '`', text.Length);
            if (closeEnd - p == length) {
                var (contentStart, contentEnd) = (runEnd, p);
                var content = text.AsSpan(contentStart, contentEnd - contentStart);
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.ContainsAnyExcept(' ')) {
                    (contentStart, contentEnd) = (contentStart + 1, contentEnd - 1);
                }
                token = new CodeToken(closeEnd, true, contentStart, contentEnd);
                break;
            }
            p = closeEnd;
        }
        _codes[i] = token;
        return token;
    }

    private LinkToken? Link(int i) {
        if (text[i] != '[') {
            return null;
        }
        if (_links.TryGetValue(i, out var cached)) {
            return cached;
        }
        var token = ScanLink(i);
        _links[i] = token;
        return token;
    }

    private LinkToken? ScanLink(int i) {
        var k = Ends().Escaped[i + 1];
        if (k + 1 >= text.Length || k == i + 1 || text[k] != ']' || text[k + 1] != '(') {
            return null;
        }
        var hrefStart = k + 2;
        var hrefEnd = hrefStart;
        while (hrefEnd < text.Length && hrefEnd - hrefStart <= MaxHrefLength && IsHrefChar(text[hrefEnd])) {
            hrefEnd++;
        }
        if (hrefEnd >= text.Length || text[hrefEnd] != ')') {
            return null;
        }
        var href = text[hrefStart..hrefEnd];
        return IsAllowedHref(href) ? new LinkToken(hrefEnd + 1, k, href) : null;
    }

    private AtomToken? Mention(int i) {
        if (!retro || text[i] != '[') {
            return null;
        }
        if (_mentions.TryGetValue(i, out var cached)) {
            return cached;
        }
        AtomToken? token = null;
        var nameStart = i + MentionPrefix.Length + 37;
        if (text.AsSpan(i).StartsWith(MentionPrefix, StringComparison.Ordinal)
            && TryParseUuid(text, i + MentionPrefix.Length, out var id)
            && nameStart < text.Length
            && text[nameStart - 1] == ':') {
            var nameEnd = ScanLabel(nameStart, MaxMentionNameLength);
            if (nameEnd > nameStart && text.AsSpan(nameEnd).StartsWith("]]", StringComparison.Ordinal)) {
                token = new AtomToken(nameEnd + 2, id, text[nameStart..nameEnd]);
            }
        }
        _mentions[i] = token;
        return token;
    }

    private AtomToken? Image(int i) {
        if (!retro || text[i] != '!') {
            return null;
        }
        if (_images.TryGetValue(i, out var cached)) {
            return cached;
        }
        AtomToken? token = null;
        if (i + 1 < text.Length && text[i + 1] == '[') {
            var altEnd = ScanLabel(i + 2, MaxImageAltLength);
            var idStart = altEnd + ImagePrefix.Length;
            if (altEnd >= 0
                && text.AsSpan(altEnd).StartsWith(ImagePrefix, StringComparison.Ordinal)
                && TryParseUuid(text, idStart, out var id)
                && idStart + 36 < text.Length
                && text[idStart + 36] == ')') {
                token = new AtomToken(idStart + 37, id, text[(i + 2)..altEnd]);
            }
        }
        _images[i] = token;
        return token;
    }

    // Raw characters up to the first ']' or newline; returns -1 when the label is longer than allowed.
    private int ScanLabel(int start, int maxLength) {
        var k = Ends().Raw[start];
        return k - start > maxLength ? -1 : k;
    }

    // For every index, the next ']' or newline: Raw counts every character, Escaped steps over backslash escapes
    // the way Parse does. Scanning from each '[' instead makes a line of 32,000 '[' quadratic.
    private sealed record Brackets(int[] Raw, int[] Escaped);

    private Brackets Ends() {
        if (_brackets != null) {
            return _brackets;
        }
        var raw = new int[text.Length + 1];
        var escaped = new int[text.Length + 1];
        raw[text.Length] = escaped[text.Length] = text.Length;
        for (var k = text.Length - 1; k >= 0; k--) {
            if (text[k] is ']' or '\n') {
                raw[k] = escaped[k] = k;
                continue;
            }
            raw[k] = raw[k + 1];
            escaped[k] = text[k] == '\\' && k + 1 < text.Length && IsAsciiPunctuation(text[k + 1])
                ? escaped[k + 2]
                : escaped[k + 1];
        }
        _brackets = new Brackets(raw, escaped);
        return _brackets;
    }

    private int RunEnd(int k, char c, int end) {
        while (k < end && text[k] == c) {
            k++;
        }
        return k;
    }

    private static void Flush(List<DocInline> output, StringBuilder pending) {
        if (pending.Length > 0) {
            output.Add(new DocText(pending.ToString()));
            pending.Clear();
        }
    }
}
