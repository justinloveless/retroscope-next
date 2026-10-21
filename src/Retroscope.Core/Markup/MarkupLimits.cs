namespace Retroscope.Core.Markup;

public enum MarkupField {
    Card,
    Comment,
    ActionItem,

    // Jira comments and new issue descriptions.
    JiraText,

    // An edited Jira description, which may carry island refs.
    JiraDescription,
}

public enum MarkupError {
    Empty,
    TooLong,
    TooManyImages,

    // NUL and other C0 controls besides tab and line breaks, or an unpaired UTF-16 surrogate:
    // Postgres text cannot hold them and they have no rendering.
    InvalidCharacter,
}

public readonly record struct MarkupCheck(Doc? Doc, MarkupError? Error) {
    public bool IsValid => Error == null;
}

public static class MarkupLimits {
    // Lengths count Unicode code points, as Postgres char_length does for the table checks (§b).
    public const int CardLength = 8000;
    public const int CommentLength = 4000;
    public const int ActionItemLength = 8000;
    public const int JiraTextLength = 32000;
    public const int ImagesPerBody = 8;

    public static int MaxLength(MarkupField field) {
        return field switch {
            MarkupField.Card => CardLength,
            MarkupField.Comment => CommentLength,
            MarkupField.ActionItem => ActionItemLength,
            MarkupField.JiraText or MarkupField.JiraDescription => JiraTextLength,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    public static MarkupDialect Dialect(MarkupField field) {
        return field switch {
            MarkupField.Card or MarkupField.Comment or MarkupField.ActionItem => MarkupDialect.Retro,
            MarkupField.JiraText => MarkupDialect.Jira,
            MarkupField.JiraDescription => MarkupDialect.JiraDescription,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    public static MarkupCheck Check(string markup, MarkupField field) {
        var codePoints = CountCodePoints(markup);
        if (codePoints < 0) {
            return new MarkupCheck(null, MarkupError.InvalidCharacter);
        }
        if (markup.AsSpan().TrimStart(" \t\r\n").IsEmpty) {
            return new MarkupCheck(null, MarkupError.Empty);
        }
        if (codePoints > MaxLength(field)) {
            return new MarkupCheck(null, MarkupError.TooLong);
        }
        var doc = MarkupParser.Parse(markup, Dialect(field));
        if (doc.Descendants().OfType<DocImage>().Count() > ImagesPerBody) {
            return new MarkupCheck(null, MarkupError.TooManyImages);
        }
        return new MarkupCheck(doc, null);
    }

    // -1 when the text holds a character no stored body may contain.
    private static int CountCodePoints(string text) {
        var count = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
            } else if (char.IsSurrogate(c) || c < ' ' && c is not ('\t' or '\n' or '\r')) {
                return -1;
            }
            count++;
        }
        return count;
    }
}
