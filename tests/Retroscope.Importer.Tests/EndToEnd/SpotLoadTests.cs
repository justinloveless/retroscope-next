extern alias server;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Team;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;
using server::Retroscope.Server.Data;
using server::Retroscope.Server.Data.Repos;
using server::Retroscope.Server.Realtime.Stores;
using static Retroscope.Importer.Tests.EndToEnd.EndToEndImport;

namespace Retroscope.Importer.Tests.EndToEnd;

/// <summary>
/// The spot check of DESIGN §h.5, in-proc against the importer's output: the target is migrated by the server
/// itself (its leader runs the migrator, as in production), imported into, and then a fresh leader loads sampled
/// rooms of every kind through the real <c>PokerStore</c>/<c>RetroStore</c>/<c>TeamStore</c> over the app role and
/// projects each for an anonymous viewer.
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection(ImporterPostgres.Name)]
public sealed class SpotLoadTests(ImporterDatabases databases) {
    private const int Sample = 20;

    // The migrator sets role-level timeouts on the role that runs it; a dedicated owner keeps them off the container's
    // superuser, whose database-level timeouts other tests depend on.
    private const string OwnerRole = "spot_owner";
    private const string AppPassword = "spot-load-app";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_sampled_room_opens_and_projects_for_an_anonymous_viewer() {
        var import = await CreateAsync(databases);
        var target = await CreateServerMigratedDatabaseAsync(import.Target);
        // The first leader migrates the empty database, as the app does when it first starts.
        var migrating = await StartLeaderAsync(target);
        await migrating.DisposeAsync();

        await import.FetchStorageAsync(target.Owner);
        await import.Runner(target.Owner).RunAsync(new RunOptions(), Ct);
        Assert.Empty(await import.VerifyAsync(target.Owner));

        await using var leader = await StartLeaderAsync(target);
        await using var dataSource = Db.CreateDataSource(target.App);
        var cursors = new CursorRepo(dataSource);
        var poker = new PokerStore(dataSource, leader, cursors);
        var retro = new RetroStore(dataSource, leader, cursors, null, NullLogger<RetroStore>.Instance);
        var teams = new TeamStore(dataSource, leader, cursors, new MembershipCache(dataSource));
        var now = DateTimeOffset.UtcNow;
        var anonymous = new PrincipalId(Guid.NewGuid());
        var client = new ClientId(Guid.NewGuid());

        var sessions = await SampleAsync(target.Owner, "poker_sessions");
        foreach (var id in sessions) {
            var state = await poker.LoadAsync(RoomKey.Poker(new SessionId(id)), now, Ct);
            Assert.True(state is not null, $"poker session {id} does not load");
            JsonSerializer.Serialize(PokerProjection.Snapshot(state, []), WireJsonContext.Default.PokerSnapshot);
            JsonSerializer.Serialize(PokerProjection.Overlay(state, anonymous, client), WireJsonContext.Default.PokerOverlay);
        }

        var boards = await SampleAsync(target.Owner, "retro_boards");
        foreach (var id in boards) {
            var state = await retro.LoadAsync(RoomKey.Board(new BoardId(id)), now, Ct);
            Assert.True(state is not null, $"board {id} does not load");
            JsonSerializer.Serialize(RetroProjection.Snapshot(state, [], forMembers: false), WireJsonContext.Default.RetroSnapshot);
        }

        var teamIds = await SampleAsync(target.Owner, "teams");
        foreach (var id in teamIds) {
            var state = await teams.LoadAsync(RoomKey.Team(new TeamId(id)), now, Ct);
            Assert.True(state is not null, $"team {id} does not load");
            JsonSerializer.Serialize(TeamProjection.Snapshot(state, now), WireJsonContext.Default.TeamSnapshot);
        }

        Assert.Equal([new Guid(TeamSession), new Guid(QuickSession)], sessions.Order());
        Assert.Equal([new Guid(TeamBoard), new Guid(QuickBoard)], boards.Order());
        Assert.Equal([new Guid(Alpha), new Guid(Beta)], teamIds.Order());

        Assert.Equal(2, (await poker.LoadAsync(RoomKey.Poker(new SessionId(new Guid(TeamSession))), now, Ct))!.Rounds.Count);
    }

    private static async Task<List<Guid>> SampleAsync(string connectionString, string table) =>
        [.. (await RowsAsync(connectionString, $"select id from {table} order by random() limit {Sample}")).Select(x => (Guid)x[0]!)];

    private static async Task<(string Owner, string App)> CreateServerMigratedDatabaseAsync(string anyTarget) {
        var admin = new NpgsqlConnectionStringBuilder(anyTarget) { Database = "postgres" }.ConnectionString;
        await ImporterDatabases.ExecuteAsync(admin, $"""
            do $$ begin
              if not exists (select from pg_roles where rolname = '{OwnerRole}') then
                create role {OwnerRole} login superuser password '{OwnerRole}';
              end if;
            end $$;
            """);
        var name = $"srv_{Guid.NewGuid():N}";
        await ImporterDatabases.ExecuteAsync(admin, $"create database {name} owner {OwnerRole}");
        var owner = new NpgsqlConnectionStringBuilder(anyTarget) { Database = name, Username = OwnerRole, Password = OwnerRole, Pooling = false };
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = Db.AppRole, Password = AppPassword, Pooling = true };
        return (owner.ConnectionString, app.ConnectionString);
    }

    private static async Task<Leader> StartLeaderAsync((string Owner, string App) target) {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DATABASE_URL"] = target.App,
                ["DATABASE_OWNER_URL"] = target.Owner,
            })
            .Build();
        var leader = new Leader(
            Db.FromConfiguration(configuration),
            new LeaderOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            new StillRunning(),
            NullLogger<Leader>.Instance);
        await leader.StartAsync(Ct);
        return leader;
    }

    private sealed class StillRunning : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() {
        }
    }
}
