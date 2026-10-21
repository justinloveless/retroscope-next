using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Data.Repos;

public sealed record TemplateColumn(string Title, ColumnColor Color, bool ActionItems);

/// <param name="MaxVotes">Votes per person; null means unlimited.</param>
public sealed record TemplateConfig(
    bool AllowAnonymous,
    bool VotingEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MaxVotes,
    bool ShowAuthorNames,
    bool StagesEnabled,
    bool EnforceReadiness,
    bool AllowSelfVotes,
    string VoteEmoji) {
    public static readonly TemplateConfig Default = new(true, true, null, true, false, false, true, "👍");
}

/// <summary>A board template: the columns and configuration a new team board starts with.</summary>
public sealed record TemplateInfo(Guid Id, string Name, bool IsDefault, IReadOnlyList<TemplateColumn> Columns, TemplateConfig Config);

/// <summary><c>board_templates</c> and <c>template_columns</c>; the columns are always written as a whole list.</summary>
public sealed class TemplatesRepo(NpgsqlDataSource dataSource) {
    public const string DefaultName = "Default";

    /// <summary>The original's default template (Good, Bad, Kudos, Action Items; <c>BoardTemplatesSettings.tsx</c>).</summary>
    public static readonly IReadOnlyList<TemplateColumn> DefaultColumns = [
        new("Good", ColumnColor.Green, false),
        new("Bad", ColumnColor.Red, false),
        new("Kudos", ColumnColor.Blue, false),
        new("Action Items", ColumnColor.Yellow, true),
    ];

    /// <summary>The template a new team starts with, as commands for the team's creating transaction.</summary>
    public static IEnumerable<NpgsqlBatchCommand> SeedDefault(TeamId team, DateTimeOffset now) {
        var template = new TemplateInfo(Guid.CreateVersion7(now), DefaultName, true, DefaultColumns, TemplateConfig.Default);
        return Insert(team, template, now);
    }

    public async Task<IReadOnlyList<TemplateInfo>> ListAsync(TeamId team, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(TeamsRepo.Command(
            """
            select id, name, is_default, allow_anonymous, voting_enabled, max_votes_per_user, show_author_names,
                   stages_enabled, enforce_readiness, allow_self_votes, vote_emoji
            from board_templates where team_id = $1
            order by is_default desc, lower(name), created_at, id
            """,
            team.Value));
        batch.BatchCommands.Add(TeamsRepo.Command(
            """
            select c.template_id, c.title, c.color, c.is_action_items
            from template_columns c join board_templates t on t.id = c.template_id
            where t.team_id = $1
            order by c.template_id, c.sort_key
            """,
            team.Value));
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var templates = new List<(Guid Id, string Name, bool IsDefault, TemplateConfig Config)>();
        while (await reader.ReadAsync(cancellationToken)) {
            templates.Add((
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                new TemplateConfig(
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetInt16(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.GetString(10))));
        }
        await reader.NextResultAsync(cancellationToken);
        var columns = new Dictionary<Guid, List<TemplateColumn>>();
        while (await reader.ReadAsync(cancellationToken)) {
            var id = reader.GetGuid(0);
            if (!columns.TryGetValue(id, out var list)) {
                columns[id] = list = [];
            }
            list.Add(new TemplateColumn(reader.GetString(1), ParseColor(reader.GetString(2)), reader.GetBoolean(3)));
        }
        return [.. templates.Select(x => new TemplateInfo(x.Id, x.Name, x.IsDefault, columns.GetValueOrDefault(x.Id) ?? [], x.Config))];
    }

    public Task CreateAsync(TeamId team, TemplateInfo template, DateTimeOffset now, CancellationToken cancellationToken) {
        return WriteAsync([.. ClearDefault(team, template), .. Insert(team, template, now)], cancellationToken);
    }

    /// <returns>False when the template is not the team's.</returns>
    public async Task<bool> ReplaceAsync(TeamId team, TemplateInfo template, DateTimeOffset now, CancellationToken cancellationToken) {
        var config = template.Config;
        var update = TeamsRepo.Command(
            """
            update board_templates set name = $3, is_default = $4, allow_anonymous = $5, voting_enabled = $6,
              max_votes_per_user = $7, show_author_names = $8, stages_enabled = $9, enforce_readiness = $10,
              allow_self_votes = $11, vote_emoji = $12, updated_at = $13
            where id = $1 and team_id = $2
            """,
            [
                template.Id, team.Value, template.Name, template.IsDefault, config.AllowAnonymous, config.VotingEnabled,
                MaxVotes(config), config.ShowAuthorNames, config.StagesEnabled, config.EnforceReadiness,
                config.AllowSelfVotes, config.VoteEmoji, now,
            ]);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var batch = new NpgsqlBatch(connection, transaction)) {
            foreach (var command in ClearDefault(team, template)) {
                batch.BatchCommands.Add(command);
            }
            batch.BatchCommands.Add(update);
            await batch.ExecuteNonQueryAsync(cancellationToken);
            if (update.RecordsAffected == 0) {
                return false;
            }
        }
        await using (var columns = new NpgsqlBatch(connection, transaction)) {
            columns.BatchCommands.Add(TeamsRepo.Command("delete from template_columns where template_id = $1", template.Id));
            foreach (var command in InsertColumns(template)) {
                columns.BatchCommands.Add(command);
            }
            await columns.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <returns>False when the template is not the team's.</returns>
    public async Task<bool> DeleteAsync(TeamId team, Guid template, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("delete from board_templates where id = $1 and team_id = $2");
        command.Parameters.Add(new() { Value = template });
        command.Parameters.Add(new() { Value = team.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static IEnumerable<NpgsqlBatchCommand> Insert(TeamId team, TemplateInfo template, DateTimeOffset now) {
        var config = template.Config;
        yield return TeamsRepo.Command(
            """
            insert into board_templates (id, team_id, name, is_default, allow_anonymous, voting_enabled, max_votes_per_user,
                                         show_author_names, stages_enabled, enforce_readiness, allow_self_votes, vote_emoji,
                                         created_at, updated_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $13)
            """,
            [
                template.Id, team.Value, template.Name, template.IsDefault, config.AllowAnonymous, config.VotingEnabled,
                MaxVotes(config), config.ShowAuthorNames, config.StagesEnabled, config.EnforceReadiness,
                config.AllowSelfVotes, config.VoteEmoji, now,
            ]);
        foreach (var command in InsertColumns(template)) {
            yield return command;
        }
    }

    private static IEnumerable<NpgsqlBatchCommand> InsertColumns(TemplateInfo template) {
        return template.Columns.Select((column, i) => TeamsRepo.Command(
            """
            insert into template_columns (id, template_id, title, color, sort_key, is_action_items)
            values ($1, $2, $3, $4, $5, $6)
            """,
            Guid.CreateVersion7(),
            template.Id,
            column.Title,
            ColorName(column.Color),
            i,
            column.ActionItems));
    }

    // board_templates_one_default_uq is not deferrable: the old default gives the flag up first.
    private static IEnumerable<NpgsqlBatchCommand> ClearDefault(TeamId team, TemplateInfo template) {
        if (template.IsDefault) {
            yield return TeamsRepo.Command(
                "update board_templates set is_default = false where team_id = $1 and is_default and id <> $2",
                team.Value,
                template.Id);
        }
    }

    private async Task WriteAsync(IEnumerable<NpgsqlBatchCommand> commands, CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var command in commands) {
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static NpgsqlParameter MaxVotes(TemplateConfig config) => new() {
        Value = config.MaxVotes is { } max ? (short)max : DBNull.Value,
        NpgsqlDbType = NpgsqlDbType.Smallint,
    };

    public static string ColorName(ColumnColor color) => color switch {
        ColumnColor.Green => "green",
        ColumnColor.Red => "red",
        ColumnColor.Blue => "blue",
        ColumnColor.Yellow => "yellow",
        ColumnColor.Purple => "purple",
        ColumnColor.Pink => "pink",
        ColumnColor.Indigo => "indigo",
        ColumnColor.Orange => "orange",
        ColumnColor.Gray => "gray",
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    private static ColumnColor ParseColor(string color) => color switch {
        "green" => ColumnColor.Green,
        "red" => ColumnColor.Red,
        "blue" => ColumnColor.Blue,
        "yellow" => ColumnColor.Yellow,
        "purple" => ColumnColor.Purple,
        "pink" => ColumnColor.Pink,
        "indigo" => ColumnColor.Indigo,
        "orange" => ColumnColor.Orange,
        _ => ColumnColor.Gray,
    };
}
