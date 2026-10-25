using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>Rows a poker room needs, written as the owner (the HTTP routes that create them belong to other units).</summary>
internal sealed class PokerSeed(TestDatabase database, TestAccess access) {
    public TestDatabase Database { get; } = database;

    public async Task<Principal> UserAsync(string name) {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id);
        await ExecuteAsync("insert into users (id, email, full_name) values ($1, $2, $3)", id, $"{id:N}@example.test", name);
        return new Principal(new PrincipalId(id), PrincipalKind.User, name, null, false);
    }

    public async Task<Principal> GuestAsync(string name) {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into principals (id, kind, display_name) values ($1, 'guest', $2)", id, name);
        return new Principal(new PrincipalId(id), PrincipalKind.Guest, name, null, false);
    }

    public async Task<TeamId> TeamAsync(params (Principal Who, TeamRole Role)[] members) {
        var team = new TeamId(Guid.CreateVersion7());
        await ExecuteAsync(
            "insert into teams (id, name, point_descriptions) values ($1, 'Team', '{\"5\":\"A week\",\"x\":3}'::jsonb)", team.Value);
        foreach (var (who, role) in members) {
            await AddMemberAsync(team, who, role);
        }
        return team;
    }

    public async Task AddMemberAsync(TeamId team, Principal who, TeamRole role) {
        var text = role switch {
            TeamRole.Owner => "owner",
            TeamRole.Admin => "admin",
            _ => "member",
        };
        await ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", team.Value, who.Id.Value, text);
        access.Set(team, who.Id, role);
    }

    public Task JiraAsync(TeamId team, string prefix = "RS", bool membersCanEdit = true) {
        return ExecuteAsync("""
            insert into team_jira_config (team_id, site_host, credentials_box, key_id, token_last4, email_masked, ticket_prefix, members_can_edit)
            values ($1, 'acme.atlassian.net', '\x00'::bytea, 'k1', 'abcd', 'j***@acme.test', $2, $3)
            """,
            team.Value, prefix, membersCanEdit);
    }

    /// <summary>A team that <paramref name="owner"/> owns, with <paramref name="members"/> as plain members.</summary>
    public Task<TeamId> TeamOfAsync(Principal owner, params Principal[] members) {
        return TeamAsync([(owner, TeamRole.Owner), .. members.Select(x => (x, TeamRole.Member))]);
    }

    /// <summary>
    /// A session of <paramref name="team"/> (every session has one, UX18) with <paramref name="keys"/>.Length rounds (a
    /// null key is a placeholder); the pointer is on round 1.
    /// </summary>
    public async Task<RoomKey> SessionAsync(Principal creator, TeamId team, params string?[] keys) {
        var session = Guid.CreateVersion7();
        if (keys.Length == 0) {
            keys = [null];
        }
        await ExecuteAsync(
            "insert into poker_sessions (id, code, team_id, created_by, next_round_number) values ($1, $2, $3, $4, $5)",
            session, $"S{session:N}"[..12], team.Value, creator.Id.Value, keys.Length + 1);
        var first = Guid.Empty;
        for (var i = 0; i < keys.Length; i++) {
            var round = Guid.CreateVersion7();
            first = i == 0 ? round : first;
            await ExecuteAsync(
                "insert into poker_rounds (id, session_id, number, sort_key, ticket_key) values ($1, $2, $3, $4, $5)",
                round, session, i + 1, i, (object?)keys[i] ?? DBNull.Value);
        }
        await ExecuteAsync("update poker_sessions set current_round_id = $2 where id = $1", session, first);
        return RoomKey.Poker(new SessionId(session));
    }

    public async Task<RoundId[]> RoundsAsync(RoomKey room) {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select id from poker_rounds where session_id = $1 order by sort_key", connection) {
            Parameters = { new() { Value = room.Id } },
        };
        var rounds = new List<RoundId>();
        await using var reader = await command.ExecuteReaderAsync(PokerHost.Token);
        while (await reader.ReadAsync(PokerHost.Token)) {
            rounds.Add(new RoundId(reader.GetGuid(0)));
        }
        return [.. rounds];
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params object[] parameters) {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(PokerHost.Token);
        while (await reader.ReadAsync(PokerHost.Token)) {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object[] parameters) {
        var rows = await RowsAsync(sql, parameters);
        return rows.Count == 0 || rows[0][0] is null ? default : (T)rows[0][0]!;
    }

    public async Task ExecuteAsync(string sql, params object[] parameters) {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        await command.ExecuteNonQueryAsync(PokerHost.Token);
    }

    private async Task<NpgsqlConnection> OpenAsync() {
        // Unpooled: every test has its own database, and an idle pool per database would use up max_connections.
        var builder = new NpgsqlConnectionStringBuilder(Database.OwnerConnectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(PokerHost.Token);
        return connection;
    }
}
