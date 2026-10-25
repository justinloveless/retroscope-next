using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Retroscope.Core.Security;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.Identity;

/// <summary>
/// Runs the identity and team stages (plus the archive stage, and a stand-in for the room stages that loads the
/// source's boards and poker sessions bare) against a fixture, and renders the result as a stable text dump.
/// </summary>
public sealed class IdentityImport {
    public const string AdminEmail = "alice@example.com";

    public static readonly string[] SourceTables = [
        "auth.users", "auth.identities", "public.profiles", "public.teams", "public.team_members", "public.team_invitations",
        "public.user_favorite_teams", "public.user_recent_activity", "public.board_templates", "public.template_columns",
        "public.endorsement_types", "public.endorsement_settings", "public.endorsements",
    ];

    private static readonly string[] TargetTables = [
        "principals", "users", "external_identities", "teams", "team_jira_config", "team_members", "team_invites",
        "board_templates", "template_columns", "endorsement_types", "endorsements", "recent_activity",
    ];

    private static readonly HashSet<string> BoxColumns = ["credentials_box", "token_box"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private IdentityImport(ImportHarness harness, ImporterSettings settings) {
        Harness = harness;
        Settings = settings;
    }

    public ImportHarness Harness { get; }

    public ImporterSettings Settings { get; }

    public Keyring Keyring => Keyring.Parse(Settings.AppKeys!);

    public ImportReport? Report { get; private set; }

    public DateTime ImportedAt { get; private set; }

    public static string FixturePath(string fixture) =>
        Path.Combine(ImporterDatabases.RepositoryRoot, "tests", "Retroscope.Importer.Tests", "Identity", "Fixtures", fixture + ".sql");

    public static string GoldenPath(string fixture) =>
        Path.Combine(ImporterDatabases.RepositoryRoot, "tests", "Retroscope.Importer.Tests", "Identity", "Golden", fixture + ".txt");

    public static async Task<IdentityImport> CreateAsync(ImporterDatabases databases, string? fixture = null) {
        var harness = await ImportHarness.CreateAsync(databases);
        if (fixture is not null) {
            await harness.SourceSqlAsync(await File.ReadAllTextAsync(FixturePath(fixture)));
        }
        var settings = ImporterDatabases.Settings(harness.Source, harness.Target) with {
            AdminEmails = new HashSet<string>(StringComparer.Ordinal) { AdminEmail },
        };
        return new IdentityImport(harness, settings);
    }

    public static IReadOnlyList<IImportStage> Stages(Action<ImportContext>? observe = null) => [
        new UsersMap(),
        new ExternalIdentitiesMap(),
        new TeamsMap(),
        new TeamMembersMap(),
        new EndorsementTypesMap(),
        new TemplatesMap(),
        new InvitesMap(),
        new DelegateStage(450, async (context, token) => {
            observe?.Invoke(context);
            await LoadRoomsAsync(context, token);
        }),
        new EndorsementsMap(),
        new RecentActivityMap(),
        new ArchiveStage(),
    ];

    public async Task<ImportReport> RunAsync(CancellationToken cancellationToken) {
        var runner = new ImportRunner(Settings, Harness.Output, new StringReader("")) {
            Stages = Stages(x => ImportedAt = x.ImportedAt),
            Checks = [],
            LeaseWait = TimeSpan.FromSeconds(2),
        };
        return Report = await runner.RunAsync(new RunOptions(), cancellationToken);
    }

    /// <summary>Target rows, archive rows and the report's tallies, counters and findings, with run-dependent values masked.</summary>
    public async Task<string> DumpAsync(CancellationToken cancellationToken) {
        var output = new StringBuilder();
        await using var connection = new NpgsqlConnection(Harness.Target);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in TargetTables) {
            output.Append("## ").AppendLine(table);
            foreach (var line in await RowsAsync(connection, $"select * from {table}", cancellationToken)) {
                output.AppendLine(line);
            }
        }

        output.AppendLine("## archive.rows");
        var archived = string.Join(", ", SourceTables.Select(x => $"'{x}', '{x}#dropped', '{x}#meta'"));
        foreach (var line in await RowsAsync(connection, $"select source_table, source_pk, row from archive.rows where source_table in ({archived})", cancellationToken)) {
            output.AppendLine(line);
        }

        var report = Report!.ToJson();
        var summary = new JsonObject {
            ["tables"] = new JsonObject(SourceTables
                .Where(x => Report.Table(x).Source > 0)
                .Select(x => KeyValuePair.Create(x, report["tables"]![x]?.DeepClone()))),
            ["counters"] = report["counters"]!.DeepClone(),
            ["findings"] = report["findings"]!.DeepClone(),
        };
        output.AppendLine("## report").AppendLine(summary.ToJsonString(Indented));
        return output.ToString().ReplaceLineEndings("\n");
    }

    private async Task<List<string>> RowsAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) {
        var lines = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={Render(reader.GetName(i), reader.GetValue(i))}");
            lines.Add(string.Join(" | ", values));
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private string Render(string column, object value) => value switch {
        DBNull => "null",
        byte[] when BoxColumns.Contains(column) => "<box>",
        byte[] bytes => Convert.ToHexStringLower(bytes),
        DateTime time when time == ImportedAt => "<import>",
        DateTime time when time == ImportedAt + InvitesMap.LinkInviteCap => "<import+7d>",
        DateTime time => time.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    // Stands in for the poker and retro stages: endorsements only need the boards to exist. Like those stages, it loads
    // only the rooms of imported teams (UX18).
    private static async Task LoadRoomsAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = new HashSet<Guid>();
        await using (var command = new NpgsqlCommand("select id from teams", context.Target))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) {
            while (await reader.ReadAsync(cancellationToken)) {
                teams.Add(reader.GetGuid(0));
            }
        }
        foreach (var board in await context.Source.ReadAsync("public.retro_boards", cancellationToken)) {
            if (board.Uuid("team_id") is { } team && teams.Contains(team)) {
                await ExecuteAsync(context, "insert into retro_boards (id, code, title, team_id) values ($1, $2, 'Board', $3)",
                    cancellationToken, board.Uuid("id")!.Value, board.Text("room_id")!, team);
            }
        }
        foreach (var session in await context.Source.ReadAsync("public.poker_sessions", cancellationToken)) {
            if (session.Uuid("team_id") is { } team && teams.Contains(team)) {
                await ExecuteAsync(context, "insert into poker_sessions (id, code, team_id) values ($1, $2, $3)",
                    cancellationToken, session.Uuid("id")!.Value, session.Text("room_id")!, team);
            }
        }
    }

    private static async Task ExecuteAsync(ImportContext context, string sql, CancellationToken cancellationToken, params object[] values) {
        await using var command = new NpgsqlCommand(sql, context.Target);
        foreach (var value in values) {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
