using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Importer.Source;

public enum TableDisposition {
    /// <summary>Rows feed the target tables; listed archive columns also go to <c>&lt;table&gt;#dropped</c>.</summary>
    Map,
    /// <summary>Every row goes to <c>archive.rows</c>.</summary>
    Archive,
    /// <summary>Only <c>count(*)</c> is ever read (foreign application data, ephemeral credentials).</summary>
    Exclude,
    /// <summary>Storage object listing: only the listed columns are required, others are ignored.</summary>
    Manifest,
}

public sealed record ExpectedTable {
    public required TableDisposition Disposition { get; init; }
    public IReadOnlyList<string> PrimaryKey { get; init; } = [];
    public IReadOnlyDictionary<string, string> Columns { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Archive { get; init; } = [];
    public IReadOnlyList<string> Seal { get; init; } = [];
    public IReadOnlyList<string> Omit { get; init; } = [];
    public IReadOnlyList<string> Html { get; init; } = [];
    public IReadOnlyList<string> Plain { get; init; } = [];
    public string ArchiveSuffix { get; init; } = "#dropped";
    public string? Reason { get; init; }
}

/// <summary>
/// <c>Map/expected-columns.json</c>: every source table the importer knows, built from the schema dump, the
/// migrations and <c>types.ts</c> (DESIGN §h.1 phase 1). Keys are <c>schema.table</c>.
/// </summary>
public sealed record ExpectedSchema {
    public const string ResourceName = "Retroscope.Importer.Map.expected-columns.json";

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Operator allow-list of unknown <c>schema.table</c> or <c>schema.table.column</c> entries, archived as is.</summary>
    public IReadOnlyList<string> AllowArchiveOnly { get; init; } = [];

    public required IReadOnlyDictionary<string, ExpectedTable> Tables { get; init; }

    public static ExpectedSchema Embedded { get; } = LoadEmbedded();

    public static ExpectedSchema Parse(string json) {
        var schema = JsonSerializer.Deserialize<ExpectedSchema>(json, Options)
            ?? throw new InvalidDataException("expected-columns.json is empty.");
        schema.Validate();
        return schema;
    }

    public ExpectedTable? Find(string table) => Tables.GetValueOrDefault(table);

    private static ExpectedSchema LoadEmbedded() {
        using var stream = typeof(ExpectedSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private void Validate() {
        foreach (var (name, table) in Tables) {
            if (table.Disposition is TableDisposition.Map or TableDisposition.Archive && table.PrimaryKey.Count == 0) {
                throw new InvalidDataException($"expected-columns.json: {name} needs a primaryKey.");
            }

            var lists = table.PrimaryKey.Concat(table.Archive).Concat(table.Seal).Concat(table.Omit).Concat(table.Html).Concat(table.Plain);
            foreach (var column in lists) {
                if (!table.Columns.ContainsKey(column)) {
                    throw new InvalidDataException($"expected-columns.json: {name} lists unknown column {column}.");
                }
            }
        }
    }
}
