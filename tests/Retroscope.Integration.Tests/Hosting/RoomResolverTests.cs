using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
[Collection(SharedPostgres.Name)]
public sealed class RoomResolverTests(PostgresFixture postgres) {
    [Fact]
    public async Task Exact_code_wins_over_a_case_insensitive_match() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        var lower = await room.InsertPokerAsync("abcdefghjk", team);
        var upper = await room.InsertPokerAsync("ABCDEFGHJK", team);

        Assert.Equal($"poker:{lower}", await room.ResolveTextAsync("poker", "abcdefghjk"));
        Assert.Equal($"poker:{upper}", await room.ResolveTextAsync("poker", "ABCDEFGHJK"));
        Assert.Equal($"poker:{lower}", await room.ResolveTextAsync("poker", "AbCdEfGhJk"));
    }

    [Fact]
    public async Task Board_facts_are_read_from_the_row() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        var board = await room.InsertBoardAsync("BOARD12345", team);

        Assert.Equal($"board:{board}", await room.ResolveTextAsync("board", "board12345"));
        var facts = Assert.Single(room.Access.Seen);
        Assert.Equal(RoomKey.Board(new BoardId(board)), facts.Room);
        Assert.Equal(new TeamId(team), facts.Team);
        Assert.False(facts.Archived);
    }

    [Fact]
    public async Task Missing_and_forbidden_rooms_get_byte_identical_responses() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        var hidden = await room.InsertBoardAsync("SECRET0001", team);
        room.Access.Deny.Add(hidden);

        var (forbiddenStatus, forbiddenHeaders, forbiddenBody) = await room.RawAsync("board", "SECRET0001");
        var (missingStatus, missingHeaders, missingBody) = await room.RawAsync("board", "NOSUCH0001");
        var (malformedStatus, malformedHeaders, malformedBody) = await room.RawAsync("board", "no_such");

        Assert.Equal(HttpStatusCode.NotFound, forbiddenStatus);
        Assert.Equal(forbiddenStatus, missingStatus);
        Assert.Equal(forbiddenStatus, malformedStatus);
        Assert.Equal(forbiddenHeaders, missingHeaders);
        Assert.Equal(forbiddenHeaders, malformedHeaders);
        Assert.Equal(forbiddenBody, missingBody);
        Assert.Equal(forbiddenBody, malformedBody);
        Assert.Contains(RoomResolver.HiddenCode, forbiddenBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleted_boards_and_rooms_of_deleted_teams_are_hidden_even_when_access_would_allow() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        await room.InsertBoardAsync("GONE000001", team, deleted: true);
        var deletedTeam = await room.InsertTeamAsync(deleted: true);
        await room.InsertBoardAsync("GONE000002", deletedTeam);
        await room.InsertPokerAsync("GONE000003", deletedTeam);

        Assert.Equal("hidden", await room.ResolveTextAsync("board", "GONE000001"));
        Assert.Equal("hidden", await room.ResolveTextAsync("board", "GONE000002"));
        Assert.Equal("hidden", await room.ResolveTextAsync("poker", "GONE000003"));
    }

    [Fact]
    public async Task Leftover_rooms_without_a_team_are_hidden_even_when_access_would_allow() {
        await using var room = await RoomAppAsync();
        await room.InsertPokerAsync("QUICK00001", team: null);
        await room.InsertQuickBoardAsync("QUICK00002");

        Assert.Equal("hidden", await room.ResolveTextAsync("poker", "QUICK00001"));
        Assert.Equal("hidden", await room.ResolveTextAsync("board", "QUICK00002"));
        Assert.Empty(room.Access.Seen);
    }

    [Fact]
    public async Task Codes_are_only_looked_up_in_their_own_room_kind() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        await room.InsertBoardAsync("ONLYBOARD1", team);

        Assert.Equal("hidden", await room.ResolveTextAsync("poker", "ONLYBOARD1"));
    }

    [Fact]
    public async Task Lookups_are_limited_to_30_per_minute_per_ip_hits_and_misses_alike() {
        await using var room = await RoomAppAsync();
        var team = await room.InsertTeamAsync(deleted: false);
        await room.InsertPokerAsync("LIMIT00001", team);

        for (var i = 0; i < 15; i++) {
            Assert.StartsWith("poker:", await room.ResolveTextAsync("poker", "LIMIT00001"), StringComparison.Ordinal);
            Assert.Equal("hidden", await room.ResolveTextAsync("poker", $"MISS{i:D6}"));
        }
        var (status, headers, body) = await room.RawAsync("poker", "LIMIT00001");

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Contains("Retry-After: 2", headers, StringComparison.Ordinal);
        Assert.Contains("rate_limited", body, StringComparison.Ordinal);
        Assert.StartsWith("poker:", await room.ResolveTextAsync("poker", "LIMIT00001", peer: "198.51.100.77"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_access_check_asks_the_policy() {
        var access = new PolicyRoomAccess(new ServiceCollection().BuildServiceProvider());
        var facts = new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), new TeamId(Guid.CreateVersion7()));
        var who = new Principal(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "Uma", null, false);

        var allowed = await access.CanSubscribeAsync(who, facts, TestContext.Current.CancellationToken);

        Assert.Equal(Policy.Can(who, Capability.Subscribe, facts, new EmptyAccessView()), allowed);
    }

    private async Task<RoomApp> RoomAppAsync() {
        var database = await postgres.CreateDatabaseAsync();
        var access = new RecordingAccess();
        var app = await TestApp.StartAsync(
            services: services => {
                services.AddSingleton(_ => Db.CreateDataSource(database.AppConnectionString));
                services.AddSingleton<IRoomAccess>(access);
            },
            map: app => app.MapGet("/test/room/{kind}/{code}", async (HttpContext context, RoomResolver resolver, string kind, string code) => {
                var resolution = await resolver.ResolveAsync(context, kind == "poker" ? RoomKind.Poker : RoomKind.Board, code, context.RequestAborted);
                return resolution is RoomResolution.Found found
                    ? Results.Text($"{found.Room}")
                    : RoomResolver.Result(resolution);
            }).RequireRoutePolicy(RoutePolicy.Public).RequireRateLimit(RateLimit.Unlimited));
        return new RoomApp(app, database, access);
    }

    private sealed class RoomApp(TestApp app, TestDatabase database, RecordingAccess access) : IAsyncDisposable {
        public RecordingAccess Access => access;

        public async Task<string> ResolveTextAsync(string kind, string code, string peer = "198.51.100.10") {
            var (status, _, body) = await RawAsync(kind, code, peer);
            return status switch {
                HttpStatusCode.OK => body,
                HttpStatusCode.NotFound => "hidden",
                _ => $"status {(int)status}: {body}",
            };
        }

        public async Task<(HttpStatusCode Status, string Headers, string Body)> RawAsync(
            string kind,
            string code,
            string peer = "198.51.100.10") {
            using var request = TestApp.Get($"/test/room/{kind}/{code}", peer);
            using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
            var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
                .Where(x => x.Key != "Date")
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"));
            return (response.StatusCode, headers, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        public async Task<Guid> InsertTeamAsync(bool deleted) {
            var id = Guid.CreateVersion7();
            await ExecuteAsync("insert into teams (id, name, deleted_at) values ($1, 'Team', $2)", id, deleted ? DateTimeOffset.UtcNow : null);
            return id;
        }

        public async Task<Guid> InsertPokerAsync(string code, Guid? team) {
            var id = Guid.CreateVersion7();
            await ExecuteAsync("insert into poker_sessions (id, code, team_id) values ($1, $2, $3)", id, code, team);
            return id;
        }

        public async Task<Guid> InsertBoardAsync(string code, Guid team, bool deleted = false) {
            var id = Guid.CreateVersion7();
            await ExecuteAsync(
                "insert into retro_boards (id, code, team_id, title, visibility, deleted_at) values ($1, $2, $3, 'Board', 'team', $4)",
                id,
                code,
                team,
                deleted ? DateTimeOffset.UtcNow : null);
            return id;
        }

        /// <summary>A team-less board as the quick-board feature left them; nobody may open one any more (UX18).</summary>
        public async Task<Guid> InsertQuickBoardAsync(string code) {
            var id = Guid.CreateVersion7();
            await ExecuteAsync("insert into retro_boards (id, code, title, visibility) values ($1, $2, 'Board', 'link')", id, code);
            return id;
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();

        private async Task ExecuteAsync(string sql, params object?[] values) {
            await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var value in values) {
                command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
            }
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingAccess : IRoomAccess {
        public HashSet<Guid> Deny { get; } = [];

        public List<ResourceFacts> Seen { get; } = [];

        public ValueTask<bool> CanSubscribeAsync(Principal? who, ResourceFacts facts, CancellationToken cancellationToken) {
            Seen.Add(facts);
            return ValueTask.FromResult(!Deny.Contains(facts.Room.Id));
        }
    }

    private sealed class EmptyAccessView : IAccessView {
        public TeamRole? RoleIn(TeamId team, PrincipalId principal) => null;
    }
}
