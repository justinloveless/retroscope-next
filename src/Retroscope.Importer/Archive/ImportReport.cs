using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retroscope.Importer.Archive;

/// <summary>Per source table: <c>source = imported + skipped{reason} + archived</c> (DESIGN §h.5).</summary>
public sealed class TableTally {
    public long Source { get; set; }
    public long Imported { get; set; }
    public long Archived { get; set; }
    public long Excluded { get; set; }
    public SortedDictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);

    public void Skip(string reason, long count = 1) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + count;

    public JsonObject ToJson() {
        var json = new JsonObject {
            ["source"] = Source,
            ["imported"] = Imported,
            ["archived"] = Archived,
            ["skipped"] = new JsonObject(Skipped.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
        };
        if (Excluded > 0) {
            json["excluded"] = Excluded;
        }
        return json;
    }
}

/// <summary>
/// The run report: stored in <c>archive.import_runs.report</c> and summarized on stdout. Hard failures are thrown;
/// everything here is either a count or a soft finding for the operator to review.
/// </summary>
public sealed class ImportReport {
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly SortedDictionary<string, TableTally> _tables = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, JsonNode?> _facts = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, JsonArray> _findings = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _counters = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TableTally> Tables => _tables;

    public TableTally Table(string sourceTable) {
        if (!_tables.TryGetValue(sourceTable, out var tally)) {
            _tables[sourceTable] = tally = new TableTally();
        }
        return tally;
    }

    /// <summary>A fact about the source or the run (fingerprint, policies, probe counts).</summary>
    public void Set(string key, JsonNode? value) => _facts[key] = value;

    public JsonNode? Fact(string key) => _facts.GetValueOrDefault(key);

    /// <summary>A soft finding listed for review, e.g. <c>demotedAdmins</c> or <c>rotatedBoardPasswords</c>.</summary>
    public void Note(string list, JsonNode item) {
        if (!_findings.TryGetValue(list, out var items)) {
            _findings[list] = items = [];
        }
        items.Add(item);
    }

    public IReadOnlyList<JsonNode?> Findings(string list) =>
        _findings.TryGetValue(list, out var items) ? [.. items] : [];

    /// <summary>A named soft-check counter, e.g. <c>nulledActorRefs.teams.creator_id</c>.</summary>
    public void Count(string counter, long by = 1) => _counters[counter] = _counters.GetValueOrDefault(counter) + by;

    public long Counter(string counter) => _counters.GetValueOrDefault(counter);

    public JsonObject ToJson() => new() {
        ["facts"] = new JsonObject(_facts.Select(x => KeyValuePair.Create(x.Key, x.Value?.DeepClone()))),
        ["tables"] = new JsonObject(_tables.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value.ToJson()))),
        ["counters"] = new JsonObject(_counters.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
        ["findings"] = new JsonObject(_findings.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value.DeepClone()))),
    };

    public string ToJsonString(bool indented = false) => ToJson().ToJsonString(indented ? Indented : null);

    public void WriteSummary(TextWriter output) {
        output.WriteLine($"{"table",-48} {"source",8} {"imported",9} {"archived",9} {"skipped",8}");
        foreach (var (name, tally) in _tables) {
            output.WriteLine($"{name,-48} {tally.Source,8} {tally.Imported,9} {tally.Archived,9} {tally.Skipped.Values.Sum(),8}");
        }
        foreach (var (name, value) in _counters) {
            output.WriteLine($"{name}: {value}");
        }
        foreach (var (name, items) in _findings) {
            output.WriteLine($"{name}: {items.Count} (see report)");
        }
    }
}
