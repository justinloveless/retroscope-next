using System.Text;
using static Retroscope.Core.Markup.MarkupSyntax;

namespace Retroscope.Core.Markup;

// Inline nodes → markup. Text escapes every delimiter character, so ambiguity can only come from neighbouring
// delimiters: the italic delimiter choice below is the one real decision.
internal sealed class InlineWriter {
    // Set when some italic had no delimiter that reads back as written; an enclosing italic then tries its
    // other delimiter, which changes what its children may use.
    private bool _ambiguous;

    public static string Write(IReadOnlyList<DocInline> nodes) => new InlineWriter().Inlines(nodes, '\0');

    // enclosingItalic: an outer italic's closer search sees every same-character run, including a nested italic's.
    private string Inlines(IReadOnlyList<DocInline> nodes, char enclosingItalic) {
        var rendered = new string[nodes.Count];
        for (var i = 0; i < nodes.Count; i++) {
            if (!IsItalic(nodes[i])) {
                rendered[i] = Inline(nodes[i], enclosingItalic);
            }
        }
        for (var first = 0; first < nodes.Count; first++) {
            if (!IsItalic(nodes[first])) {
                continue;
            }
            var last = first;
            while (last + 1 < nodes.Count && IsItalic(nodes[last + 1])) {
                last++;
            }
            RenderItalics(nodes, rendered, first, last, enclosingItalic);
            first = last;
        }
        for (var i = 0; i + 1 < nodes.Count; i++) {
            if (nodes[i] is DocText && rendered[i].EndsWith('!') && rendered[i + 1].StartsWith('[')) {
                rendered[i] = rendered[i][..^1] + "\\!";
            }
        }
        return string.Concat(rendered);
    }

    private static bool IsItalic(DocNode node) => node is DocStyled { Style: DocStyle.Italic };

    private static bool IsBold(DocNode? node) => node is DocStyled { Style: DocStyle.Bold };

    // Picks '_' or '*' for a run of adjacent italics. Neighbours must differ ("_a__b_" and "*a**b*" both read as
    // one italic). '_' is no delimiter next to a letter or digit or inside an italic '_' opened; '*' next to a
    // bold run, or before a bold first child, makes a '***' that the parser reads as bold first. '_' is tried
    // first because it reads best.
    private void RenderItalics(IReadOnlyList<DocInline> nodes, string[] rendered, int first, int last, char enclosingItalic) {
        var count = last - first + 1;
        var allowed = AllowedDelimiters(nodes, rendered, first, last, enclosingItalic);
        var ambiguousBefore = _ambiguous;
        string[]? fallback = null;
        foreach (var start in "_*") {
            var delimiters = Enumerable.Range(0, count).Select(i => (i % 2 == 0) == (start == '_') ? '_' : '*').ToArray();
            if (!delimiters.Select((d, i) => d == '_' ? allowed[i].Underscore : allowed[i].Star).All(ok => ok)) {
                continue;
            }
            _ambiguous = false;
            var texts = RenderItalicRun(nodes, first, delimiters);
            if (!_ambiguous) {
                texts.CopyTo(rendered, first);
                _ambiguous = ambiguousBefore;
                return;
            }
            fallback ??= texts;
        }
        fallback ??= RenderItalicRun(nodes, first, allowed.Select(a => a.Underscore ? '_' : '*').ToArray());
        fallback.CopyTo(rendered, first);
        _ambiguous = true;
    }

    private string[] RenderItalicRun(IReadOnlyList<DocInline> nodes, int first, char[] delimiters) {
        return delimiters
            .Select((d, i) => d + Inlines(((DocStyled)nodes[first + i]).Content, d) + d)
            .ToArray();
    }

    private static (bool Underscore, bool Star)[] AllowedDelimiters(
        IReadOnlyList<DocInline> nodes,
        string[] rendered,
        int first,
        int last,
        char enclosingItalic) {
        var count = last - first + 1;
        var before = first > 0 ? rendered[first - 1] : "";
        var after = last + 1 < nodes.Count ? rendered[last + 1] : "";
        var allowed = new (bool Underscore, bool Star)[count];
        for (var i = 0; i < count; i++) {
            var content = ((DocStyled)nodes[first + i]).Content;
            allowed[i] = (
                enclosingItalic != '_'
                    && (i > 0 || before.Length == 0 || !IsAsciiAlphanumeric(before[^1]))
                    && (i < count - 1 || after.Length == 0 || !IsAsciiAlphanumeric(after[0])),
                enclosingItalic != '*'
                    && (i > 0 || first == 0 || !IsBold(nodes[first - 1]))
                    && (i < count - 1 || last + 1 == nodes.Count || !IsBold(nodes[last + 1]))
                    && (content.Count == 0 || !IsBold(content[0])));
        }
        return allowed;
    }

    private string Inline(DocInline node, char enclosingItalic) {
        return node switch {
            DocText text => EscapeText(text.Value),
            DocLineBreak => "\n",
            DocStyled { Style: DocStyle.Bold } bold => $"**{Inlines(bold.Content, enclosingItalic)}**",
            DocStyled { Style: DocStyle.Strike } strike => $"~~{Inlines(strike.Content, enclosingItalic)}~~",
            DocCode code => WriteCode(code.Value),
            DocLink { Title: not null } => throw MarkupWriter.Unrepresentable(node),
            DocLink link => $"[{(link.Content.Count > 0 ? Inlines(link.Content, '\0') : EscapeText(link.Href))}]({EncodeHref(link.Href)})",
            DocMention mention => $"{MentionPrefix}{mention.UserId:D}:{Label(mention.Name, MaxMentionNameLength, "user")}]]",
            DocImage image => $"![{Label(image.Alt, MaxImageAltLength, "")}{ImagePrefix}{image.BlobId:D})",
            _ => throw MarkupWriter.Unrepresentable(node),
        };
    }

    private static string WriteCode(string value) {
        value = value.Replace('\n', ' ');
        if (value.Length == 0) {
            return "";
        }
        var (longestRun, run) = (0, 0);
        foreach (var c in value) {
            run = c == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }
        var fence = new string('`', longestRun + 1);
        var pad = value[0] == '`' || value[^1] == '`' || value[0] == ' ' && value[^1] == ' ' && value.AsSpan().ContainsAnyExcept(' ');
        return pad ? $"{fence} {value} {fence}" : $"{fence}{value}{fence}";
    }

    private static string EscapeText(string value) {
        var output = new StringBuilder(value.Length);
        foreach (var c in value) {
            if (c is '\\' or '*' or '_' or '~' or '`' or '[' or ']') {
                output.Append('\\');
            }
            output.Append(c);
        }
        return output.ToString();
    }

    private static string EncodeHref(string href) {
        return href.Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("<", "%3C", StringComparison.Ordinal)
            .Replace(">", "%3E", StringComparison.Ordinal);
    }

    private static string Label(string value, int maxLength, string fallback) {
        var cleaned = new string(value.Where(c => c is not (']' or '\n' or '\r')).Take(maxLength).ToArray());
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}
