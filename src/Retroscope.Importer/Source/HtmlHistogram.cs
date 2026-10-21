using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Retroscope.Importer.Source;

public sealed class UnknownHtmlTagsException(IReadOnlyList<string> errors)
    : Exception("HTML histogram found tags the converter does not handle:" + Environment.NewLine
        + string.Join(Environment.NewLine, errors.Select(x => "  - " + x))
        + Environment.NewLine + "Add each tag to Source/html-textonly.json if its text is all that matters.") {
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Phase 3 (DESIGN §h.1): parse every HTML column of the snapshot and count <c>(tag, attribute)</c> pairs. Any tag
/// outside <see cref="HtmlTagPolicy"/> fails the run. Escaped text such as <c>&amp;lt;div&amp;gt;</c> is text, not a tag.
/// </summary>
public sealed class HtmlHistogram {
    private const int SamplesPerTag = 5;

    private readonly SortedDictionary<string, long> _tags = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _attributes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, List<string>> _unknown = new(StringComparer.Ordinal);
    private readonly HtmlParser _parser = new();
    private readonly IElement _context;

    public HtmlHistogram() {
        _context = _parser.ParseDocument(string.Empty).Body!;
    }

    public IReadOnlyDictionary<string, long> Tags => _tags;

    public IReadOnlyDictionary<string, long> Attributes => _attributes;

    public IReadOnlyCollection<string> UnknownTags => _unknown.Keys;

    public static async Task<HtmlHistogram> BuildAsync(SourceSnapshot snapshot, CancellationToken cancellationToken) {
        var histogram = new HtmlHistogram();
        foreach (var plan in snapshot.Layout.Tables.Values.Where(x => x.Expected.Html.Count > 0).OrderBy(x => x.Name, StringComparer.Ordinal)) {
            foreach (var row in await snapshot.ReadAsync(plan.Name, cancellationToken)) {
                foreach (var column in plan.Expected.Html) {
                    histogram.Add(row.Text(column), $"{plan.Name}.{column}#{row.PrimaryKey}");
                }
            }
        }
        return histogram;
    }

    public void Add(string? html, string location) {
        if (string.IsNullOrEmpty(html) || !html.Contains('<', StringComparison.Ordinal)) {
            return;
        }

        foreach (var node in _parser.ParseFragment(html, _context)) {
            foreach (var element in node.DescendantsAndSelf<IElement>()) {
                var tag = element.LocalName;
                _tags[tag] = _tags.GetValueOrDefault(tag) + 1;
                foreach (var attribute in element.Attributes) {
                    var key = $"{tag}[{attribute.LocalName}]";
                    _attributes[key] = _attributes.GetValueOrDefault(key) + 1;
                }
                if (!HtmlTagPolicy.IsKnown(tag)) {
                    if (!_unknown.TryGetValue(tag, out var samples)) {
                        _unknown[tag] = samples = [];
                    }
                    if (samples.Count < SamplesPerTag) {
                        samples.Add(location);
                    }
                }
            }
        }
    }

    public void ThrowIfUnknown() {
        if (_unknown.Count > 0) {
            throw new UnknownHtmlTagsException([.. _unknown.Select(x => $"<{x.Key}> × {_tags[x.Key]} (e.g. {string.Join(", ", x.Value)})")]);
        }
    }

    public JsonObject ToJson() => new() {
        ["tags"] = new JsonObject(_tags.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
        ["attributes"] = new JsonObject(_attributes.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
    };
}
