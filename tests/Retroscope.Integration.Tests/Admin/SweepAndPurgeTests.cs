using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data;
using Retroscope.Server.Jobs;
using static Retroscope.Integration.Tests.Admin.AdminHost;
using static Retroscope.Integration.Tests.Admin.RetentionTests;

namespace Retroscope.Integration.Tests.Admin;

[Trait("Category", "Admin")]
[Collection(SharedPostgres.Name)]
public sealed class SweepAndPurgeTests(PostgresFixture postgres) {
    [Fact]
    public async Task The_sweep_deletes_expired_sessions_tokens_and_dead_invites_and_closes_swept_sessions() {
        await using var host = await StartAsync(postgres);
        var user = await host.CreateUserAsync("u@example.com");
        var now = host.Time.GetUtcNow();
        var idle = await SessionAsync(host, user.Id, idle: now - TimeSpan.FromSeconds(1), absolute: now + TimeSpan.FromDays(1));
        var absolute = await SessionAsync(host, user.Id, idle: now + TimeSpan.FromDays(1), absolute: now);
        var guest = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into principals (id, kind, display_name) values ($1, 'guest', 'Guest')", guest);
        await host.ExecuteAsync("insert into guest_tokens (token_hash, principal_id, expires_at) values ($1, $2, $3)", Hash(), guest, now - TimeSpan.FromMinutes(1));
        await host.ExecuteAsync("insert into guest_tokens (token_hash, principal_id, expires_at) values ($1, $2, $3)", Hash(), guest, now + TimeSpan.FromDays(1));
        await host.ExecuteAsync("""
            insert into auth_tokens (token_hash, purpose, user_id, email, expires_at) values ($1, 'magic_link', $2, 'u@example.com', $3)
            """, Hash(), user.Id.Value, now - TimeSpan.FromMinutes(1));
        await host.ExecuteAsync("""
            insert into auth_tokens (token_hash, purpose, user_id, email, expires_at) values ($1, 'password_reset', $2, 'u@example.com', $3)
            """, Hash(), user.Id.Value, now + TimeSpan.FromMinutes(10));
        var team = await AdminEndpointTests.TeamAsync(host, "Team", user.Id);
        var expired = await InviteAsync(host, team, now - TimeSpan.FromMinutes(1), revoked: false);
        var revoked = await InviteAsync(host, team, now + TimeSpan.FromDays(3), revoked: true);
        var live = await InviteAsync(host, team, now + TimeSpan.FromDays(3), revoked: false);

        var report = await host.Service<TokenSweepJob>().RunAsync(Token);

        Assert.Equal(new TokenSweepReport(2, 1, 1, 2), report);
        // The user's own session from CreateUserAsync is live and stays.
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from guest_tokens"));
        Assert.Equal(["password_reset"], await StringsAsync(host, "select purpose from auth_tokens"));
        Assert.Equal([live.ToString()], await StringsAsync(host, "select id::text from team_invites"));
        Assert.DoesNotContain(expired.ToString(), await StringsAsync(host, "select id::text from team_invites"));
        Assert.DoesNotContain(revoked.ToString(), await StringsAsync(host, "select id::text from team_invites"));
        Assert.Equal(new[] { Sessions.PublicId(idle), Sessions.PublicId(absolute) }.Order(), host.Revoker.AuthSessions.Order());
    }

    [Fact]
    public async Task A_team_is_hard_deleted_with_everything_it_owns_thirty_days_after_its_soft_delete() {
        await using var host = await StartAsync(postgres);
        var owner = await host.CreateUserAsync("owner@example.com");
        var now = host.Time.GetUtcNow();
        var doomed = await AdminEndpointTests.TeamAsync(host, "Doomed", owner.Id);
        var recent = await AdminEndpointTests.TeamAsync(host, "Recent", owner.Id);
        var live = await AdminEndpointTests.TeamAsync(host, "Live", owner.Id);
        await host.ExecuteAsync("update teams set deleted_at = $2 where id = $1", doomed.Value, now - TimeSpan.FromDays(31));
        await host.ExecuteAsync("update teams set deleted_at = $2 where id = $1", recent.Value, now - TimeSpan.FromDays(29));
        var session = await RetentionTests.SessionAsync(host, "TEAMS1", now, doomed);
        var board = await BoardAsync(host, "TEAMB1", now, doomed);
        var image = await ImageAsync(host, board, attached: true, now);
        var liveSession = await RetentionTests.SessionAsync(host, "LIVES1", now, live);
        await InviteAsync(host, doomed, now + TimeSpan.FromDays(3), revoked: false);
        await CursorAsync(host, RoomKey.Team(doomed), owner.Id, now);
        await CursorAsync(host, RoomKey.Poker(session), owner.Id, now);
        await CursorAsync(host, RoomKey.Board(board), owner.Id, now);
        await CursorAsync(host, RoomKey.Poker(liveSession), owner.Id, now);
        await host.ExecuteAsync("insert into recent_activity (user_id, entity_kind, entity_id) values ($1, 'board', $2)", owner.Id.Value, board.Value);
        await host.ExecuteAsync("insert into recent_activity (user_id, entity_kind, entity_id) values ($1, 'team', $2)", owner.Id.Value, doomed.Value);
        await host.ExecuteAsync("insert into recent_activity (user_id, entity_kind, entity_id) values ($1, 'team', $2)", owner.Id.Value, live.Value);

        var report = await host.Service<TeamPurgeJob>().RunAsync(Token);

        Assert.Equal(new TeamPurgeReport(1, 0), report);
        Assert.False(await ExistsAsync(host, "teams", doomed.Value));
        Assert.True(await ExistsAsync(host, "teams", recent.Value));
        Assert.True(await ExistsAsync(host, "teams", live.Value));
        Assert.False(await ExistsAsync(host, "poker_sessions", session.Value));
        Assert.False(await ExistsAsync(host, "retro_boards", board.Value));
        Assert.False(await ExistsAsync(host, "blobs", image));
        Assert.True(await ExistsAsync(host, "poker_sessions", liveSession.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from team_members where team_id = $1", doomed.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from team_invites where team_id = $1", doomed.Value));
        Assert.Equal([RoomKey.Poker(liveSession).ToString()], await StringsAsync(host, "select room_key from client_cursors"));
        Assert.Equal([live.Value.ToString()], await StringsAsync(host, "select entity_id::text from recent_activity"));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'team.purge' and team_id = $1 and target = $2", doomed.Value, doomed.Value.ToString()));
        Assert.Equal(new TeamPurgeReport(0, 0), await host.Service<TeamPurgeJob>().RunAsync(Token));
    }

    [Fact]
    public async Task The_team_purge_pages_past_its_page_size() {
        await using var host = await StartAsync(postgres);
        var owner = await host.CreateUserAsync("owner@example.com");
        var now = host.Time.GetUtcNow();
        for (var i = 0; i < 3; i++) {
            var team = await AdminEndpointTests.TeamAsync(host, $"Doomed {i}", owner.Id);
            await host.ExecuteAsync("update teams set deleted_at = $2 where id = $1", team.Value, now - TimeSpan.FromDays(31 + i));
        }
        var job = new TeamPurgeJob(
            host.Service<NpgsqlDataSource>(),
            host.Service<Leader>(),
            host.Registry,
            host.Time,
            host.App.Services,
            NullLogger<TeamPurgeJob>.Instance) { PageSize = 1 };

        Assert.Equal(new TeamPurgeReport(3, 0), await job.RunAsync(Token));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from teams"));
    }

    [Fact]
    public async Task A_server_that_loses_leadership_mid_purge_stops_deleting_teams() {
        var host = await StartAsync(postgres);
        try {
            var owner = await host.CreateUserAsync("owner@example.com");
            var now = host.Time.GetUtcNow();
            for (var i = 0; i < 3; i++) {
                var team = await AdminEndpointTests.TeamAsync(host, $"Doomed {i}", owner.Id);
                await host.ExecuteAsync("update teams set deleted_at = $2 where id = $1", team.Value, now - TimeSpan.FromDays(31 + i));
            }
            await TakeOverOnDeleteAsync(host, "teams");

            await Assert.ThrowsAsync<FencingLostException>(() => host.Service<TeamPurgeJob>().RunAsync(Token));

            Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from teams"));
        } finally {
            await DisposeAfterLossAsync(host);
        }
    }

    [Fact]
    public async Task The_team_purge_finds_recent_activity_and_cursors_by_index() {
        await using var host = await StartAsync(postgres);
        var owner = await host.CreateUserAsync("owner@example.com");
        var now = host.Time.GetUtcNow();
        var team = await AdminEndpointTests.TeamAsync(host, "Doomed", owner.Id);
        await host.ExecuteAsync("""
            insert into recent_activity (user_id, entity_kind, entity_id)
            select $1, (array['team', 'board', 'poker'])[1 + n % 3], gen_random_uuid() from generate_series(1, 5000) n
            """, owner.Id.Value);
        await host.ExecuteAsync("analyze recent_activity");

        // Plans are judged with sequential scans priced out, so only an index-friendly predicate avoids one.
        await using var connection = new NpgsqlConnection(host.Database.AppConnectionString);
        await connection.OpenAsync(Token);
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection)) {
            await off.ExecuteNonQueryAsync(Token);
        }
        await using var explain = new NpgsqlCommand("explain (format json) " + TeamPurgeJob.PurgeSql, connection) {
            Parameters = {
                new NpgsqlParameter<Guid> { TypedValue = team.Value },
                new NpgsqlParameter<DateTimeOffset> { TypedValue = now },
                new NpgsqlParameter<DateTimeOffset> { TypedValue = now },
            },
        };
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync(Token))!);

        var nodes = Nodes(plan.RootElement).ToList();
        var indexes = nodes.Select(node => node.TryGetProperty("Index Name", out var index) ? index.GetString() : null).ToList();
        Assert.Contains("recent_activity_entity_ix", indexes);
        Assert.Contains("client_cursors_pkey", indexes);
        Assert.DoesNotContain(nodes, node => node.GetProperty("Node Type").GetString() == "Seq Scan"
            && node.GetProperty("Relation Name").GetString() is "recent_activity" or "client_cursors");
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Array) {
            return element.EnumerateArray().SelectMany(Nodes);
        }
        if (element.ValueKind != JsonValueKind.Object) {
            return [];
        }
        var self = element.TryGetProperty("Node Type", out _) ? [element] : Array.Empty<JsonElement>();
        return self.Concat(element.EnumerateObject().SelectMany(property => Nodes(property.Value)));
    }

    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    private static async Task<byte[]> SessionAsync(AdminHost host, PrincipalId user, DateTimeOffset idle, DateTimeOffset absolute) {
        var hash = Hash();
        await host.ExecuteAsync(
            "insert into sessions (id_hash, user_id, auth_method, idle_expires_at, abs_expires_at) values ($1, $2, 'password', $3, $4)",
            hash,
            user.Value,
            idle,
            absolute);
        return hash;
    }

    private static async Task<Guid> InviteAsync(AdminHost host, TeamId team, DateTimeOffset expires, bool revoked) {
        var id = Guid.CreateVersion7();
        await host.ExecuteAsync("""
            insert into team_invites (id, team_id, kind, email, token_hash, expires_at, revoked_at)
            values ($1, $2, 'email', 'x@example.com', $3, $4, $5)
            """, id, team.Value, Hash(), expires, revoked ? expires - TimeSpan.FromDays(1) : DBNull.Value);
        return id;
    }
}
