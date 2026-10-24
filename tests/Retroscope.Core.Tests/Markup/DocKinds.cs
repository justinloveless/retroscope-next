using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

// The node kinds each dialect may produce; anything else in a parsed Doc is a grammar leak. Display-only attributes
// (block format, link titles) count as kinds of their own.
internal static class DocKinds {
    public static string? DisallowedIn(Doc doc, MarkupDialect dialect) {
        foreach (var node in doc.Descendants()) {
            var allowed = node switch {
                DocParagraph paragraph => paragraph.Format == default,
                DocBulletList or DocOrderedList or DocListItem or DocQuote or DocCodeBlock => true,
                DocText or DocCode or DocLineBreak => true,
                DocLink link => link.Title == null,
                DocStyled styled => styled.Style is not DocStyle.Underline,
                DocMention or DocImage => dialect == MarkupDialect.Retro,
                DocHeading heading => dialect != MarkupDialect.Retro && heading.Format == default,
                DocRule => dialect != MarkupDialect.Retro,
                DocIsland => dialect == MarkupDialect.JiraDescription && doc.Blocks.Contains(node),
                _ => false,
            };
            if (!allowed) {
                return node.GetType().Name;
            }
        }
        return null;
    }
}
