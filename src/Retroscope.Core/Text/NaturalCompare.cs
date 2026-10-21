namespace Retroscope.Core.Text;

/// <summary>
/// The order of JavaScript's <c>a.localeCompare(b, undefined, { numeric: true })</c>, which the original used
/// for Jira sprint buckets, without ICU (the server runs with InvariantGlobalization, F22). Digit runs compare
/// by value, ignoring leading zeros. Other characters compare case-insensitively with ICU's root classes:
/// spaces and ASCII punctuation (in ICU order) before digits before letters. Remaining ties go to the first
/// case difference, lower case first. Non-ASCII letters compare by their upper-case code point, which is an
/// approximation of ICU (accented letters do not sort next to their base letter).
/// </summary>
public sealed class NaturalCompare : IComparer<string?> {
    public static readonly NaturalCompare Instance = new();

    // ICU root collation order of the ASCII characters that are neither letters nor digits.
    private const string PunctuationOrder = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$";

    private const int PunctuationClass = 0;
    private const int DigitClass = 1;
    private const int LetterClass = 2;

    public static int Compare(string? a, string? b) {
        if (ReferenceEquals(a, b)) {
            return 0;
        }
        if (a is null) {
            return -1;
        }
        if (b is null) {
            return 1;
        }
        var primary = ComparePrimary(a, b);
        return primary != 0 ? primary : CompareCase(a, b);
    }

    int IComparer<string?>.Compare(string? x, string? y) => Compare(x, y);

    private static int ComparePrimary(string a, string b) {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length) {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j])) {
                var numeric = CompareDigitRuns(a, ref i, b, ref j);
                if (numeric != 0) {
                    return numeric;
                }
                continue;
            }
            var byKey = CharKey(a[i]).CompareTo(CharKey(b[j]));
            if (byKey != 0) {
                return byKey;
            }
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static int CompareDigitRuns(string a, ref int i, string b, ref int j) {
        var (aStart, aEnd) = DigitRun(a, ref i);
        var (bStart, bEnd) = DigitRun(b, ref j);
        var byLength = (aEnd - aStart).CompareTo(bEnd - bStart);
        if (byLength != 0) {
            return byLength;
        }
        return a.AsSpan(aStart, aEnd - aStart).SequenceCompareTo(b.AsSpan(bStart, bEnd - bStart));
    }

    /// <summary>Consumes a digit run and returns the span of its significant digits.</summary>
    private static (int Start, int End) DigitRun(string s, ref int index) {
        while (index < s.Length - 1 && s[index] == '0' && char.IsAsciiDigit(s[index + 1])) {
            index++;
        }
        var start = index;
        while (index < s.Length && char.IsAsciiDigit(s[index])) {
            index++;
        }
        return (start, index);
    }

    private static long CharKey(char c) {
        var punctuation = PunctuationOrder.IndexOf(c, StringComparison.Ordinal);
        if (punctuation >= 0) {
            return Key(PunctuationClass, punctuation);
        }
        if (char.IsAsciiDigit(c)) {
            return Key(DigitClass, c);
        }
        if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) {
            return Key(PunctuationClass, PunctuationOrder.Length + c);
        }
        return Key(LetterClass, char.ToUpperInvariant(c));
    }

    private static long Key(int characterClass, int rank) => ((long)characterClass << 32) | (uint)rank;

    private static int CompareCase(string a, string b) {
        var length = Math.Min(a.Length, b.Length);
        for (var k = 0; k < length; k++) {
            if (a[k] != b[k] && char.ToUpperInvariant(a[k]) == char.ToUpperInvariant(b[k])) {
                return char.IsLower(a[k]) ? -1 : 1;
            }
        }
        return 0;
    }
}
