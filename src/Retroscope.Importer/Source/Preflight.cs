using System.Text.Json.Nodes;
using Npgsql;
using Retroscope.Importer.Archive;

namespace Retroscope.Importer.Source;

public sealed class PreflightFailedException(IReadOnlyList<string> errors)
    : Exception("Preflight failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(x => "  - " + x))) {
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>The effective layout of one source table after preflight (allow-listed unknown columns are archived).</summary>
public sealed record TablePlan(SourceTable Source, ExpectedTable Expected) {
    public string Name => Source.Name;

    public IEnumerable<string> ReadableColumns => Source.Columns.Select(x => x.Name).Where(x => !Expected.Omit.Contains(x));
}

public sealed class PreflightResult {
    public required SourceCatalog Catalog { get; init; }
    public required IReadOnlyDictionary<string, TablePlan> Tables { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public TablePlan Plan(string table) =>
        Tables.GetValueOrDefault(table) ?? throw new InvalidOperationException($"{table} is not part of the import plan.");

    public IEnumerable<TablePlan> WithDisposition(TableDisposition disposition) =>
        Tables.Values.Where(x => x.Expected.Disposition == disposition).OrderBy(x => x.Name, StringComparer.Ordinal);
}

/// <summary>
/// Phase 1 (DESIGN §h.1): compare the live source with <c>expected-columns.json</c>. A missing expected table or
/// column, a changed type or primary key, an unknown public table or column that is not allow-listed as archive-only,
/// and any unlisted <c>auth.*</c> column of a table we read all fail the run (S25: sealing must be explicit).
/// </summary>
public static class Preflight {
    public static async Task<PreflightResult> RunAsync(
        NpgsqlConnection source,
        ExpectedSchema expected,
        ImportReport report,
        CancellationToken cancellationToken) {
        var catalog = await SourceCatalog.ReadAsync(source, cancellationToken);
        var result = Compare(catalog, expected);
        await RecordFactsAsync(source, report, cancellationToken);
        foreach (var warning in result.Warnings) {
            report.Note("preflightWarnings", warning);
        }
        return result;
    }

    public static PreflightResult Compare(SourceCatalog catalog, ExpectedSchema expected) {
        var errors = new List<string>();
        var warnings = new List<string>();
        var plans = new Dictionary<string, TablePlan>(StringComparer.Ordinal);
        var allowed = expected.AllowArchiveOnly.ToHashSet(StringComparer.Ordinal);

        foreach (var (name, table) in expected.Tables.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            var actual = catalog.Find(name);
            if (actual is null) {
                errors.Add($"expected table {name} is missing");
                continue;
            }
            if (table.Disposition == TableDisposition.Exclude) {
                plans[name] = new TablePlan(actual, table);
                continue;
            }

            foreach (var (column, type) in table.Columns) {
                var found = actual.Column(column);
                if (found is null) {
                    errors.Add($"expected column {name}.{column} is missing");
                } else if (found.Type != type) {
                    errors.Add($"column {name}.{column} is {found.Type}, expected {type}");
                }
            }
            if (table.PrimaryKey.Count > 0 && !table.PrimaryKey.SequenceEqual(actual.PrimaryKey)) {
                errors.Add($"primary key of {name} is ({string.Join(", ", actual.PrimaryKey)}), expected ({string.Join(", ", table.PrimaryKey)})");
            }

            var extra = new List<string>();
            foreach (var column in actual.Columns.Where(x => !table.Columns.ContainsKey(x.Name))) {
                if (table.Disposition == TableDisposition.Manifest) {
                    continue;
                }
                if (actual.Schema == "auth") {
                    errors.Add($"unlisted auth column {name}.{column.Name}: add it to the mapped or sealed lists");
                } else if (allowed.Contains($"{name}.{column.Name}")) {
                    extra.Add(column.Name);
                } else {
                    errors.Add($"unknown column {name}.{column.Name}");
                }
            }
            plans[name] = new TablePlan(actual, extra.Count == 0 ? table : WithArchivedColumns(table, actual, extra));
        }

        foreach (var actual in catalog.Tables.Where(x => !expected.Tables.ContainsKey(x.Name))) {
            switch (actual.Schema) {
                case "public" when allowed.Contains(actual.Name) && actual.PrimaryKey.Count > 0:
                    plans[actual.Name] = new TablePlan(actual, ArchiveOnly(actual));
                    break;
                case "public" when allowed.Contains(actual.Name):
                    errors.Add($"allow-listed table {actual.Name} has no primary key");
                    break;
                case "public":
                    errors.Add($"unknown table {actual.Name}");
                    break;
                case "auth":
                    warnings.Add($"auth table {actual.Name} is not read (not in expected-columns.json)");
                    break;
                default:
                    break;
            }
        }

        if (errors.Count > 0) {
            throw new PreflightFailedException(errors);
        }
        return new PreflightResult { Catalog = catalog, Tables = plans, Warnings = warnings };
    }

    private static ExpectedTable WithArchivedColumns(ExpectedTable table, SourceTable actual, List<string> extra) => table with {
        Columns = actual.Columns.Where(x => table.Columns.ContainsKey(x.Name) || extra.Contains(x.Name)).ToDictionary(x => x.Name, x => x.Type),
        Archive = table.Disposition == TableDisposition.Map ? [.. table.Archive, .. extra] : table.Archive,
    };

    private static ExpectedTable ArchiveOnly(SourceTable actual) => new() {
        Disposition = TableDisposition.Archive,
        PrimaryKey = actual.PrimaryKey,
        Columns = actual.Columns.ToDictionary(x => x.Name, x => x.Type),
        Reason = "allow-listed archive-only table",
    };

    // cron.job, pg_policies and the GoTrue autoconfirm probe (S1) go into the report for the operator to compare
    // with the dump; they never change what is imported.
    private static async Task RecordFactsAsync(NpgsqlConnection source, ImportReport report, CancellationToken cancellationToken) {
        report.Set("serverVersion", await ScalarAsync<string>(source, "show server_version", cancellationToken));
        report.Set("systemIdentifier", await SystemIdentifierAsync(source, cancellationToken));
        report.Set("autoconfirmProbe", await ScalarAsync<long>(source,
            "select count(*) from auth.users where email_confirmed_at is not null and confirmation_sent_at is null",
            cancellationToken));
        var hasCron = await ScalarAsync<bool>(source, "select to_regclass('cron.job') is not null", cancellationToken);
        report.Set("cronJobs", hasCron
            ? await RowsAsync(source, "select jobname, schedule, command, active from cron.job order by jobid", cancellationToken)
            : null);
        report.Set("policies", await RowsAsync(source,
            "select tablename, policyname, cmd, roles::text, qual, with_check from pg_policies where schemaname = 'public' order by tablename, policyname",
            cancellationToken));
    }

    private static async Task<string?> SystemIdentifierAsync(NpgsqlConnection source, CancellationToken cancellationToken) {
        try {
            return (await ScalarAsync<long>(source, "select system_identifier from pg_control_system()", cancellationToken))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        } catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege) {
            return null;
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection source, string sql, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(sql, source);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<JsonArray> RowsAsync(NpgsqlConnection source, string sql, CancellationToken cancellationToken) {
        var rows = new JsonArray();
        await using var command = new NpgsqlCommand(sql, source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++) {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : JsonValue.Create(reader.GetValue(i).ToString());
            }
            rows.Add(row);
        }
        return rows;
    }
}
