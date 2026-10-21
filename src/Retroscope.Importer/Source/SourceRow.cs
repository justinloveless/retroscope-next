using System.Globalization;

namespace Retroscope.Importer.Source;

/// <summary>
/// One source row as read from the snapshot. Values are CLR-typed: uuid → <see cref="Guid"/>, text and enums →
/// <see cref="string"/>, json/jsonb → raw JSON <see cref="string"/>, timestamptz → UTC <see cref="DateTime"/>,
/// numeric → <see cref="decimal"/>, arrays → CLR arrays; types without a mapping are read as their text form.
/// </summary>
public sealed class SourceRow {
    private readonly Dictionary<string, int> _ordinals;
    private readonly object?[] _values;

    internal SourceRow(TablePlan plan, Dictionary<string, int> ordinals, object?[] values) {
        Plan = plan;
        _ordinals = ordinals;
        _values = values;
    }

    public TablePlan Plan { get; }

    public string Table => Plan.Name;

    public IEnumerable<string> Columns => _ordinals.Keys;

    /// <summary>The canonical primary-key text used as <c>archive.rows.source_pk</c>: values joined with <c>|</c>.</summary>
    public string PrimaryKey => string.Join('|', Plan.Expected.PrimaryKey.Select(x => Format(this[x])));

    public object? this[string column] =>
        _ordinals.TryGetValue(column, out var ordinal)
            ? _values[ordinal]
            : throw new KeyNotFoundException($"{Table} has no readable column {column}.");

    public bool Has(string column) => _ordinals.ContainsKey(column);

    public T? Get<T>(string column) => this[column] is { } value ? (T)value : default;

    public Guid? Uuid(string column) => (Guid?)this[column];

    public string? Text(string column) => (string?)this[column];

    public bool? Bool(string column) => (bool?)this[column];

    public int? Number(string column) => this[column] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;

    public DateTime? Timestamp(string column) => (DateTime?)this[column];

    public string TypeOf(string column) => Plan.Source.Column(column)?.Type ?? "text";

    /// <summary>The value as plain text: what gets sealed, and what a composite primary key is joined from.</summary>
    public static string Format(object? value) => value switch {
        null => "",
        string text => text,
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
