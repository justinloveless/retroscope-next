namespace Retroscope.Server.Endpoints;

/// <summary>The plain-text rule shared by names, nicknames and team texts, whoever enters them.</summary>
public static class TextRules {
    /// <summary>No control characters, no lone surrogates, and no bidi embeddings or overrides that reorder the text.</summary>
    public static bool IsPlainText(string value) {
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            if (char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069') {
                return false;
            }
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                i++;
            } else if (char.IsSurrogate(c)) {
                return false;
            }
        }
        return true;
    }
}
