using Npgsql;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

/// <summary>
/// Migration 0003 (UX18) on a database that ran 0001 and 0002 and holds everything it removes: quick rooms with rows
/// on them, link and password team boards, access grants, guest tokens, favorites, and board and poker activity.
/// </summary>
[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class TeamOnlyRoomsMigrationTests(PostgresFixture postgres) {
    private const string BcryptHash = "$2a$10$abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0";

    private readonly Guid _member = Guid.NewGuid();
    private readonly Guid _guest = Guid.NewGuid();
    private readonly Guid _slack = Guid.NewGuid();
    private readonly Guid _team = Guid.NewGuid();
    private readonly Guid _teamSession = Guid.NewGuid();
    private readonly Guid _teamRound = Guid.NewGuid();
    private readonly Guid _quickSession = Guid.NewGuid();
    private readonly Guid _quickRound = Guid.NewGuid();
    private readonly Guid _teamBoard = Guid.NewGuid();
    private readonly Guid _linkBoard = Guid.NewGuid();
    private readonly Guid _linkColumn = Guid.NewGuid();
    private readonly Guid _linkCard = Guid.NewGuid();
    private readonly Guid _linkImage = Guid.NewGuid();
    private readonly Guid _passwordBoard = Guid.NewGuid();
    private readonly Guid _quickBoard = Guid.NewGuid();
    private readonly Guid _quickColumn = Guid.NewGuid();
    private readonly Guid _quickCard = Guid.NewGuid();
    private readonly Guid _quickImage = Guid.NewGuid();
    private readonly Guid _quickPasswordBoard = Guid.NewGuid();
    private readonly Guid _actionItem = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Quick_rooms_go_with_their_rows_cursors_and_activity_and_team_rooms_stay() {
        var database = await SeededAt0002Async();

        Assert.Equal(1, await postgres.MigrateAsync(database, new Migrator(Migrator.FromEmbeddedScripts().Scripts.Take(3))));

        Assert.Equal([_teamSession], await IdsAsync(database, "select id from poker_sessions"));
        Assert.Equal([_teamRound], await IdsAsync(database, "select id from poker_rounds"));
        Assert.Equal([_teamRound], await IdsAsync(database, "select round_id from poker_votes"));
        Assert.Empty(await IdsAsync(database, "select session_id from poker_observers"));
        Assert.Equal(
            new[] { _teamBoard, _linkBoard, _passwordBoard }.Order(),
            await IdsAsync(database, "select id from retro_boards"));
        Assert.Equal([_linkColumn], await IdsAsync(database, "select id from retro_columns"));
        Assert.Equal([_linkCard], await IdsAsync(database, "select id from retro_cards"));
        Assert.Equal([_linkBoard], await IdsAsync(database, "select board_id from retro_votes"));
        Assert.Empty(await IdsAsync(database, "select id from retro_comments"));
        Assert.Empty(await IdsAsync(database, "select board_id from retro_readiness"));
        Assert.Empty(await IdsAsync(database, "select board_id from mentions"));
        Assert.Equal([_linkImage], await IdsAsync(database, "select id from blobs"));
        Assert.True(await ScalarAsync<bool>(database, $"""
            select source_board_id is null and source_card_id is null from team_action_items where id = '{_actionItem}'
            """));

        Assert.Equal(
            new[] { $"board:{_linkBoard}", $"poker:{_teamSession}", $"team:{_team}" },
            await StringsAsync(database, "select room_key from client_cursors order by room_key"));
        Assert.Equal(
            [$"team:{_team}"],
            await StringsAsync(database, "select entity_kind || ':' || entity_id from recent_activity"));
        Assert.Equal(
            new[] { _member, _guest, _slack }.Order(),
            await IdsAsync(database, "select id from principals"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "select count(*) from users"));
    }

    [Fact]
    public async Task Rooms_need_a_team_and_board_access_guest_tokens_and_favorites_are_gone() {
        var database = await SeededAt0002Async();

        await postgres.MigrateAsync(database, Migrator.FromEmbeddedScripts());

        foreach (var gone in new[] {
            "board_access_grants", "guest_tokens", "team_favorites", "poker_sessions_quick_idle_ix", "retro_boards_quick_idle_ix",
        }) {
            Assert.False(await ScalarAsync<bool>(database, $"select to_regclass('public.{gone}') is not null"), gone);
        }
        Assert.Empty(await StringsAsync(database, """
            select column_name from information_schema.columns
            where table_name = 'retro_boards' and column_name in ('visibility', 'password_hash', 'password_set_at')
            """));
        Assert.Equal(
            ["NO", "NO"],
            await StringsAsync(database, """
                select is_nullable from information_schema.columns
                where table_name in ('poker_sessions', 'retro_boards') and column_name = 'team_id'
                """));

        var quick = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, "insert into poker_sessions (id, code) values (gen_random_uuid(), 'LATE01')"));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, quick.SqlState);
        var board = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, "insert into retro_boards (id, code, title) values (gen_random_uuid(), 'LATEB1', 'Late')"));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, board.SqlState);
        var activity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            insert into recent_activity (user_id, entity_kind, entity_id) values ('{_member}', 'board', '{_linkBoard}')
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, activity.SqlState);
        await ExecuteAsync(database, $"""
            insert into retro_boards (id, code, team_id, title) values (gen_random_uuid(), 'LATEB2', '{_team}', 'Late')
            """);
    }

    private async Task<TestDatabase> SeededAt0002Async() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        await postgres.MigrateAsync(database, new Migrator(Migrator.FromEmbeddedScripts().Scripts.Take(2)));
        await ExecuteAsync(database, $$"""
            insert into principals (id, kind, display_name) values
              ('{{_member}}', 'user', null), ('{{_guest}}', 'guest', 'Guest'), ('{{_slack}}', 'external', 'Slack user');
            insert into users (id, email, full_name) values ('{{_member}}', 'member@example.com', 'Member');
            insert into teams (id, name) values ('{{_team}}', 'Team');
            insert into team_members (team_id, user_id, role) values ('{{_team}}', '{{_member}}', 'owner');
            insert into team_favorites (user_id, team_id) values ('{{_member}}', '{{_team}}');
            insert into guest_tokens (token_hash, principal_id, expires_at) values (sha256('guest'), '{{_guest}}', now() + interval '1 day');

            insert into poker_sessions (id, code, team_id) values
              ('{{_teamSession}}', 'TEAMS1', '{{_team}}'), ('{{_quickSession}}', 'QUICK1', null);
            insert into poker_rounds (id, session_id, number, sort_key) values
              ('{{_teamRound}}', '{{_teamSession}}', 1, 0), ('{{_quickRound}}', '{{_quickSession}}', 1, 0);
            update poker_sessions
              set current_round_id = '{{_quickRound}}', spotlight_holder_id = '{{_guest}}', spotlight_round_id = '{{_quickRound}}'
              where id = '{{_quickSession}}';
            insert into poker_votes (round_id, principal_id, points) values
              ('{{_teamRound}}', '{{_member}}', 3), ('{{_quickRound}}', '{{_guest}}', 5), ('{{_quickRound}}', '{{_slack}}', 8);
            insert into poker_observers (session_id, principal_id) values ('{{_quickSession}}', '{{_member}}');

            insert into retro_boards (id, code, team_id, title, visibility, password_hash, password_set_at) values
              ('{{_teamBoard}}', 'TEAMB1', '{{_team}}', 'Team', 'team', null, null),
              ('{{_linkBoard}}', 'LINKB1', '{{_team}}', 'Link', 'link', null, null),
              ('{{_passwordBoard}}', 'PASSB1', '{{_team}}', 'Password', 'password', '{{BcryptHash}}', now()),
              ('{{_quickBoard}}', 'QUICKB', null, 'Quick', 'link', null, null),
              ('{{_quickPasswordBoard}}', 'QUICKP', null, 'Quick password', 'password', '{{BcryptHash}}', now());
            insert into retro_columns (id, board_id, title, color, sort_key) values
              ('{{_linkColumn}}', '{{_linkBoard}}', 'Went well', 'green', 0), ('{{_quickColumn}}', '{{_quickBoard}}', 'Went well', 'green', 0);
            insert into retro_cards (id, board_id, column_id, body, author_id) values
              ('{{_linkCard}}', '{{_linkBoard}}', '{{_linkColumn}}', 'Link card', '{{_member}}'),
              ('{{_quickCard}}', '{{_quickBoard}}', '{{_quickColumn}}', 'Quick card', '{{_guest}}');
            update retro_boards set focus_card_id = '{{_quickCard}}' where id = '{{_quickBoard}}';
            insert into retro_comments (id, card_id, board_id, body, author_id) values
              (gen_random_uuid(), '{{_quickCard}}', '{{_quickBoard}}', 'Comment', '{{_member}}');
            insert into retro_votes (card_id, voter_id, board_id) values
              ('{{_linkCard}}', '{{_guest}}', '{{_linkBoard}}'), ('{{_quickCard}}', '{{_member}}', '{{_quickBoard}}');
            insert into retro_readiness (board_id, stage, principal_id) values ('{{_quickBoard}}', 'thinking', '{{_guest}}');
            insert into mentions (source_kind, source_id, board_id, user_id) values ('card', '{{_quickCard}}', '{{_quickBoard}}', '{{_member}}');
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes) values
              ('{{_linkImage}}', 'board_image', '{{_linkBoard}}', 'image/png', 1, sha256('x'), 'x'),
              ('{{_quickImage}}', 'board_image', '{{_quickBoard}}', 'image/png', 1, sha256('x'), 'x');
            insert into board_access_grants (board_id, principal_id, password_epoch) values
              ('{{_passwordBoard}}', '{{_guest}}', now()), ('{{_quickPasswordBoard}}', '{{_member}}', now());
            insert into team_action_items (id, team_id, text, source_board_id, source_card_id) values
              ('{{_actionItem}}', '{{_team}}', 'Follow up', '{{_quickBoard}}', '{{_quickCard}}');

            insert into recent_activity (user_id, entity_kind, entity_id) values
              ('{{_member}}', 'team', '{{_team}}'), ('{{_member}}', 'board', '{{_linkBoard}}'), ('{{_member}}', 'board', '{{_quickBoard}}'),
              ('{{_member}}', 'poker', '{{_teamSession}}'), ('{{_member}}', 'poker', '{{_quickSession}}');
            insert into client_cursors (room_key, client_id, principal_id, last_cseq) values
              ('team:{{_team}}', gen_random_uuid(), '{{_member}}', 1),
              ('poker:{{_teamSession}}', gen_random_uuid(), '{{_member}}', 1),
              ('poker:{{_quickSession}}', gen_random_uuid(), '{{_guest}}', 1),
              ('board:{{_linkBoard}}', gen_random_uuid(), '{{_guest}}', 1),
              ('board:{{_quickBoard}}', gen_random_uuid(), '{{_member}}', 1),
              ('board:{{_quickPasswordBoard}}', gen_random_uuid(), '{{_member}}', 1);
            """);
        return database;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The query's first column, ordered here so the comparison does not depend on the database's uuid order.</summary>
    private static async Task<List<Guid>> IdsAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var values = new List<Guid>();
        while (await reader.ReadAsync(Token)) {
            values.Add(reader.GetGuid(0));
        }
        return [.. values.Order()];
    }

    private static async Task<List<string>> StringsAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var values = new List<string>();
        while (await reader.ReadAsync(Token)) {
            values.Add(reader.GetString(0));
        }
        return values;
    }
}
