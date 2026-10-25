using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Jobs;
using static Retroscope.Integration.Tests.Admin.AdminHost;

namespace Retroscope.Integration.Tests.Admin;

[Trait("Category", "Admin")]
[Collection(SharedPostgres.Name)]
public sealed class RetentionTests(PostgresFixture postgres) {
    /// <summary>Every room belongs to a team (UX18): only the team purge removes rooms, however long they sit idle.</summary>
    [Fact]
    public async Task Idle_rooms_stay_with_their_cursors_and_activity() {
        await using var host = await StartAsync(postgres);
        var user = await host.CreateUserAsync("u@example.com");
        var now = host.Time.GetUtcNow();
        var team = await AdminEndpointTests.TeamAsync(host, "Team", user.Id);
        var session = await SessionAsync(host, "IDLE01", now - TimeSpan.FromDays(365), team);
        var board = await BoardAsync(host, "IDLEB1", now - TimeSpan.FromDays(365), team);
        await CursorAsync(host, RoomKey.Poker(session), user.Id, now);
        await CursorAsync(host, RoomKey.Board(board), user.Id, now);
        await host.ExecuteAsync("insert into recent_activity (user_id, entity_kind, entity_id) values ($1, 'team', $2)", user.Id.Value, team.Value);

        await host.Service<RetentionJob>().RunAsync(Token);

        Assert.True(await ExistsAsync(host, "poker_sessions", session.Value));
        Assert.True(await ExistsAsync(host, "retro_boards", board.Value));
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from client_cursors"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from recent_activity"));
    }

    [Fact]
    public async Task A_server_that_loses_leadership_mid_pass_stops_deleting() {
        var host = await StartAsync(postgres);
        try {
            var user = await host.CreateUserAsync("u@example.com");
            var now = host.Time.GetUtcNow();
            var team = await AdminEndpointTests.TeamAsync(host, "Team", user.Id);
            var board = await BoardAsync(host, "FENCE1", now, team);
            await CursorAsync(host, RoomKey.Board(board), user.Id, now - TimeSpan.FromDays(15));
            var loose = await ImageAsync(host, board, attached: false, now - TimeSpan.FromHours(25));
            await TakeOverOnDeleteAsync(host, "client_cursors");

            await Assert.ThrowsAsync<FencingLostException>(() => host.Service<RetentionJob>().RunAsync(Token));

            Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from client_cursors"));
            Assert.True(await ExistsAsync(host, "blobs", loose));
            Assert.False(host.Service<Leader>().IsLeader);
        } finally {
            await DisposeAfterLossAsync(host);
        }
    }

    [Fact]
    public async Task Stale_cursors_unattached_images_dead_mail_and_old_audit_rows_are_removed() {
        await using var host = await StartAsync(postgres);
        var user = await host.CreateUserAsync("u@example.com");
        var now = host.Time.GetUtcNow();
        var board = await BoardAsync(host, "BOARD1", now, await AdminEndpointTests.TeamAsync(host, "Team", user.Id));
        await CursorAsync(host, RoomKey.Board(board), user.Id, now - TimeSpan.FromDays(15));
        await CursorAsync(host, RoomKey.Poker(new SessionId(Guid.NewGuid())), user.Id, now - TimeSpan.FromDays(13));
        var oldLoose = await ImageAsync(host, board, attached: false, now - TimeSpan.FromHours(25));
        var newLoose = await ImageAsync(host, board, attached: false, now - TimeSpan.FromHours(23));
        var oldAttached = await ImageAsync(host, board, attached: true, now - TimeSpan.FromDays(3));
        await MailAsync(host, body: null, now - TimeSpan.FromDays(8));
        await MailAsync(host, body: null, now - TimeSpan.FromDays(6));
        await MailAsync(host, body: "still to send", now - TimeSpan.FromDays(30));
        await host.ExecuteAsync("insert into audit_log (at, action) values ($1, 'old')", now - TimeSpan.FromDays(181));
        await host.ExecuteAsync("insert into audit_log (at, action) values ($1, 'recent')", now - TimeSpan.FromDays(179));

        var report = await host.Service<RetentionJob>().RunAsync(Token);

        Assert.Equal(1, report.Cursors);
        Assert.Equal(1, report.Blobs);
        Assert.Equal(1, report.DeadMails);
        Assert.Equal(1, report.AuditRows);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from client_cursors"));
        Assert.Equal(
            new[] { newLoose, oldAttached }.Order(),
            (await StringsAsync(host, "select id::text from blobs")).Select(Guid.Parse).Order());
        Assert.DoesNotContain(oldLoose, (await StringsAsync(host, "select id::text from blobs")).Select(Guid.Parse));
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
        Assert.Equal(["recent"], await StringsAsync(host, "select action from audit_log"));
    }

    [Fact]
    public async Task Unattached_images_are_collected_in_batches_until_none_is_left() {
        await using var host = await StartAsync(postgres);
        var now = host.Time.GetUtcNow();
        var user = await host.CreateUserAsync("u@example.com");
        var board = await BoardAsync(host, "BOARD1", now, await AdminEndpointTests.TeamAsync(host, "Team", user.Id));
        await host.ExecuteAsync("""
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, created_at)
            select gen_random_uuid(), 'board_image', $1, 'image/png', 1, sha256('x'), 'x', $2
            from generate_series(1, $3)
            """, board.Value, now - TimeSpan.FromDays(2), RetentionJob.BlobBatch * 2 + 7);

        var report = await host.Service<RetentionJob>().RunAsync(Token);

        Assert.Equal(RetentionJob.BlobBatch * 2 + 7, report.Blobs);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    internal static async Task<SessionId> SessionAsync(AdminHost host, string code, DateTimeOffset lastActivity, TeamId team) {
        var id = new SessionId(Guid.CreateVersion7());
        await host.ExecuteAsync(
            "insert into poker_sessions (id, code, team_id, last_activity_at) values ($1, $2, $3, $4)",
            id.Value,
            code,
            team.Value,
            lastActivity);
        return id;
    }

    internal static async Task<BoardId> BoardAsync(AdminHost host, string code, DateTimeOffset lastActivity, TeamId team) {
        var id = new BoardId(Guid.CreateVersion7());
        await host.ExecuteAsync(
            "insert into retro_boards (id, code, team_id, title, last_activity_at) values ($1, $2, $3, 'Board', $4)",
            id.Value,
            code,
            team.Value,
            lastActivity);
        return id;
    }

    internal static async Task<Guid> ImageAsync(AdminHost host, BoardId board, bool attached, DateTimeOffset createdAt) {
        var id = Guid.NewGuid();
        await host.ExecuteAsync("""
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, attached_at, created_at)
            values ($1, 'board_image', $2, 'image/png', 1, sha256('x'), 'x', $3, $4)
            """, id, board.Value, attached ? createdAt : DBNull.Value, createdAt);
        return id;
    }

    internal static Task CursorAsync(AdminHost host, RoomKey room, PrincipalId principal, DateTimeOffset updated) {
        return host.ExecuteAsync(
            "insert into client_cursors (room_key, client_id, principal_id, last_cseq, updated_on) values ($1, $2, $3, 1, $4)",
            room.ToString(),
            Guid.NewGuid(),
            principal.Value,
            DateOnly.FromDateTime(updated.UtcDateTime));
    }

    internal static async Task<bool> ExistsAsync(AdminHost host, string table, Guid id) {
        return await host.ScalarAsync<bool>($"select exists (select 1 from {table} where id = $1)", id);
    }

    internal static async Task<List<string>> StringsAsync(AdminHost host, string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(host.Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        await using var reader = await command.ExecuteReaderAsync(Token);
        var values = new List<string>();
        while (await reader.ReadAsync(Token)) {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    /// <summary>Another instance becomes leader in the transaction of the first delete from <paramref name="table"/>.</summary>
    internal static Task TakeOverOnDeleteAsync(AdminHost host, string table) {
        return host.ExecuteAsync($$"""
            create function take_over() returns trigger language plpgsql security definer as $$
            begin
              update system_state set leader_epoch = leader_epoch + 1;
              return null;
            end $$;
            create trigger take_over after delete on {{table}} for each statement execute function take_over();
            """);
    }

    /// <summary>A host whose leader was fenced off stops with <see cref="LeadershipLostException"/>, as designed.</summary>
    internal static async Task DisposeAfterLossAsync(AdminHost host) {
        try {
            await host.DisposeAsync();
        } catch (Exception e) when (e is LeadershipLostException || e.InnerException is LeadershipLostException) {
        }
    }

    private static Task MailAsync(AdminHost host, string? body, DateTimeOffset createdAt) {
        return host.ExecuteAsync(
            "insert into email_outbox (to_address, subject, body_text, created_at) values ('a@b.c', 's', $1, $2)",
            (object?)body ?? DBNull.Value,
            createdAt);
    }
}
