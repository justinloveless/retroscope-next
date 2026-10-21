using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Retroscope.Fakes.FakeJira;

/// <summary>Jira answers a query it cannot parse with 400; so does the fake for JQL outside its subset.</summary>
public sealed class FakeJqlException(string message) : Exception(message);

/// <summary>A parsed query: which issues match, and whether the query asked for rank order.</summary>
public sealed record FakeJqlQuery(Func<JsonObject, bool> Matches, bool ByRank);

/// <summary>
/// The JQL subset Retroscope sends (DESIGN §f, <c>Jql.cs</c>), evaluated against the fake's issues: <c>AND</c>,
/// <c>OR</c> and parentheses over <c>project</c>, <c>key</c>, <c>issuetype</c>, <c>status</c>,
/// <c>statusCategory</c>, <c>sprint</c> (ids, <c>openSprints()</c>, <c>futureSprints()</c>, <c>EMPTY</c>),
/// <c>summary ~</c> and <c>cf[NNNNN]</c> clauses, then an optional <c>ORDER BY Rank</c>. As in Jira, <c>!=</c> and
/// <c>not in</c> never match an empty field.
/// </summary>
public sealed partial class FakeJql {
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase) {
        "project", "key", "issuetype", "status", "statusCategory", "sprint", "summary",
    };

    private readonly List<Token> _tokens;
    private readonly string _sprintFieldId;
    private readonly Func<long, string?> _sprintState;
    private int _at;

    private FakeJql(List<Token> tokens, string sprintFieldId, Func<long, string?> sprintState) {
        _tokens = tokens;
        _sprintFieldId = sprintFieldId;
        _sprintState = sprintState;
    }

    /// <param name="sprintState">The current state of a sprint by id (issues hold copies taken when they moved).</param>
    /// <exception cref="FakeJqlException">The query is outside the subset.</exception>
    public static FakeJqlQuery Parse(string jql, string sprintFieldId, Func<long, string?> sprintState) {
        var parser = new FakeJql(Tokenize(jql), sprintFieldId, sprintState);
        var matches = parser.Peek() is not { } first || (first.Kind == TokenKind.Word && first.Is("ORDER"))
            ? _ => true
            : parser.Or();
        var byRank = parser.TakeWord("ORDER");
        if (byRank) {
            parser.ExpectWord("BY");
            parser.ExpectWord("Rank");
            parser.ExpectWord("ASC");
        }
        if (parser.Peek() is { } extra) {
            throw new FakeJqlException($"Unexpected '{extra.Text}' in the JQL query.");
        }
        return new FakeJqlQuery(matches, byRank);
    }

    private Func<JsonObject, bool> Or() {
        var left = And();
        while (TakeWord("OR")) {
            var (a, b) = (left, And());
            left = issue => a(issue) || b(issue);
        }
        return left;
    }

    private Func<JsonObject, bool> And() {
        var left = Unary();
        while (TakeWord("AND")) {
            var (a, b) = (left, Unary());
            left = issue => a(issue) && b(issue);
        }
        return left;
    }

    private Func<JsonObject, bool> Unary() {
        if (!TakeSymbol("(")) {
            return Clause();
        }
        var inner = Or();
        ExpectSymbol(")");
        return inner;
    }

    private Func<JsonObject, bool> Clause() {
        var field = Next(TokenKind.Word).Text;
        if (!Fields.Contains(field) && CustomField(field) is null) {
            throw new FakeJqlException($"Field '{field}' does not exist or you do not have permission to view it.");
        }
        if (TakeWord("IS")) {
            var negated = TakeWord("NOT");
            ExpectWord("EMPTY");
            return issue => IsEmpty(field, issue) != negated;
        }
        if (TakeWord("NOT")) {
            ExpectWord("IN");
            var excluded = List();
            return issue => !IsEmpty(field, issue) && !excluded.Any(value => Is(field, issue, value));
        }
        if (TakeWord("IN")) {
            var included = List();
            return issue => included.Any(value => Is(field, issue, value));
        }
        if (TakeSymbol("~")) {
            var words = ReadValue().Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return issue => Text(field, issue) is { } text && words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
        if (TakeSymbol("!=")) {
            var value = ReadValue();
            return issue => !IsEmpty(field, issue) && !Is(field, issue, value);
        }
        ExpectSymbol("=");
        var expected = ReadValue();
        return issue => Is(field, issue, expected);
    }

    private List<JqlValue> List() {
        if (!TakeSymbol("(")) {
            return [ReadValue()];
        }
        var values = new List<JqlValue> { ReadValue() };
        while (TakeSymbol(",")) {
            values.Add(ReadValue());
        }
        ExpectSymbol(")");
        return values;
    }

    private JqlValue ReadValue() {
        var token = Peek() ?? throw new FakeJqlException("The JQL query ends early.");
        _at++;
        if (token.Kind == TokenKind.Word && TakeSymbol("(")) {
            ExpectSymbol(")");
            return new JqlValue(token.Text, IsFunction: true);
        }
        return token.Kind switch {
            TokenKind.String => new JqlValue(Unescape(token.Text), IsFunction: false),
            TokenKind.Word or TokenKind.Number => new JqlValue(token.Text, IsFunction: false),
            _ => throw new FakeJqlException($"Expected a value but got '{token.Text}'."),
        };
    }

    private bool Is(string field, JsonObject issue, JqlValue value) {
        var fields = issue["fields"]!.AsObject();
        if (CustomField(field) is { } custom) {
            return fields[custom] is JsonValue number
                && decimal.TryParse(value.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var expected)
                && number.GetValue<decimal>() == expected;
        }
        if (value.IsFunction) {
            return (field.ToLowerInvariant(), value.Text.ToLowerInvariant()) switch {
                ("issuetype", "subtaskissuetypes") => (bool?)fields["issuetype"]?["subtask"] == true,
                ("sprint", "opensprints") => SprintIds(fields).Any(id => _sprintState(id) == "active"),
                ("sprint", "futuresprints") => SprintIds(fields).Any(id => _sprintState(id) == "future"),
                ("sprint", "closedsprints") => SprintIds(fields).Any(id => _sprintState(id) == "closed"),
                _ => throw new FakeJqlException($"Function '{value.Text}()' is not supported for '{field}'."),
            };
        }
        return field.ToLowerInvariant() switch {
            "project" => Same((string?)fields["project"]?["key"], value.Text),
            "key" => Same((string?)issue["key"], value.Text),
            "issuetype" => Same((string?)fields["issuetype"]?["name"], value.Text),
            "status" => Same((string?)fields["status"]?["name"], value.Text),
            "statuscategory" => Same((string?)fields["status"]?["statusCategory"]?["name"], value.Text)
                || Same((string?)fields["status"]?["statusCategory"]?["key"], value.Text),
            "sprint" => long.TryParse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? SprintIds(fields).Contains(id)
                : SprintNames(fields).Any(name => Same(name, value.Text)),
            _ => throw new FakeJqlException($"Operator '=' is not supported for '{field}'."),
        };
    }

    private bool IsEmpty(string field, JsonObject issue) {
        var fields = issue["fields"]!.AsObject();
        if (CustomField(field) is { } custom) {
            return fields[custom] is null;
        }
        return field.ToLowerInvariant() switch {
            "sprint" => !SprintIds(fields).Any(),
            "summary" => string.IsNullOrEmpty((string?)fields["summary"]),
            _ => false,
        };
    }

    private static string? Text(string field, JsonObject issue) =>
        string.Equals(field, "summary", StringComparison.OrdinalIgnoreCase)
            ? (string?)issue["fields"]!["summary"]
            : throw new FakeJqlException($"Operator '~' is not supported for '{field}'.");

    private IEnumerable<long> SprintIds(JsonObject fields) =>
        fields[_sprintFieldId] is JsonArray sprints ? sprints.Select(sprint => (long)sprint!["id"]!) : [];

    private IEnumerable<string?> SprintNames(JsonObject fields) =>
        fields[_sprintFieldId] is JsonArray sprints ? sprints.Select(sprint => (string?)sprint!["name"]) : [];

    private static bool Same(string? actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? CustomField(string field) {
        var match = CustomFieldRef().Match(field);
        return match.Success ? "customfield_" + match.Groups[1].Value : null;
    }

    private Token? Peek() => _at < _tokens.Count ? _tokens[_at] : null;

    private Token Next(TokenKind kind) {
        if (Peek() is not { } token || token.Kind != kind) {
            throw new FakeJqlException($"Expected a {kind} at token {_at} of the JQL query.");
        }
        _at++;
        return token;
    }

    private bool TakeWord(string word) {
        if (Peek() is { Kind: TokenKind.Word } token && token.Is(word)) {
            _at++;
            return true;
        }
        return false;
    }

    private bool TakeSymbol(string symbol) {
        if (Peek() is { Kind: TokenKind.Symbol } token && token.Text == symbol) {
            _at++;
            return true;
        }
        return false;
    }

    private void ExpectWord(string word) {
        if (!TakeWord(word)) {
            throw new FakeJqlException($"Expected '{word}' at token {_at} of the JQL query.");
        }
    }

    private void ExpectSymbol(string symbol) {
        if (!TakeSymbol(symbol)) {
            throw new FakeJqlException($"Expected '{symbol}' at token {_at} of the JQL query.");
        }
    }

    private static List<Token> Tokenize(string jql) {
        var tokens = new List<Token>();
        var at = 0;
        while (at < jql.Length) {
            var match = TokenPattern().Match(jql, at);
            if (!match.Success || match.Index != at) {
                throw new FakeJqlException($"The JQL query is not valid at character {at}.");
            }
            at += match.Length;
            if (match.Groups["string"].Success) {
                tokens.Add(new Token(TokenKind.String, match.Groups["string"].Value));
            } else if (match.Groups["symbol"].Success) {
                tokens.Add(new Token(TokenKind.Symbol, match.Groups["symbol"].Value));
            } else if (match.Groups["word"].Success) {
                tokens.Add(new Token(TokenKind.Word, match.Groups["word"].Value));
            } else if (match.Groups["number"].Success) {
                tokens.Add(new Token(TokenKind.Number, match.Groups["number"].Value));
            }
        }
        return tokens;
    }

    private static string Unescape(string quoted) {
        var builder = new StringBuilder(quoted.Length);
        for (var i = 1; i < quoted.Length - 1; i++) {
            if (quoted[i] == '\\') {
                i++;
            }
            builder.Append(quoted[i]);
        }
        return builder.ToString();
    }

    [GeneratedRegex("""\s*(?:(?<string>"(?:[^"\\]|\\.)*")|(?<symbol>!=|=|~|\(|\)|,)|(?<number>-?[0-9]+(?:\.[0-9]+)?)|(?<word>[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?))\s*""")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^cf\[([0-9]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex CustomFieldRef();

    private enum TokenKind {
        String,
        Symbol,
        Word,
        Number,
    }

    private sealed record Token(TokenKind Kind, string Text) {
        public bool Is(string word) => string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record JqlValue(string Text, bool IsFunction);
}
