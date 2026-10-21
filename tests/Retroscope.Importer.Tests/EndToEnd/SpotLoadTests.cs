extern alias server;

using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;
using server::Retroscope.Server.Data;
using server::Retroscope.Server.Data.Repos;
using server::Retroscope.Server.Hosting;
using server::Retroscope.Server.Realtime.Stores;
using static Retroscope.Importer.Tests.EndToEnd.EndToEndImport;

namespace Retroscope.Importer.Tests.EndToEnd;

/// <summary>
/// The spot check of DESIGN §h.5, in-proc against the importer's output: the target is migrated by the server
/// itself (its leader runs the migrator, as in production), imported into, and then the server's own
/// <c>SpotLoadCommand</c> (what <c>retroscope spot-load</c> runs) loads sampled rooms of every kind through the real
/// stores over the app role and checks the <c>snap</c> frame an anonymous viewer would get.
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
        await using var services = SpotLoadServices(dataSource, leader);
        var output = new StringWriter();

        var report = await SpotLoadCommand.RunAsync(services, Sample, output, Ct);

        Assert.True(report.Ok, output.ToString());
        Assert.Equal(
            new Dictionary<RoomKind, int> { [RoomKind.Poker] = 2, [RoomKind.Board] = 2, [RoomKind.Team] = 2 },
            report.Loaded);
        Assert.Equal("spot-load: poker 2, board 2, team 2 loaded; 0 failed", output.ToString().Trim());
        var poker = services.GetRequiredService<PokerStore>();
        Assert.Equal(2, (await poker.LoadAsync(RoomKey.Poker(new SessionId(new Guid(TeamSession))), DateTimeOffset.UtcNow, Ct))!.Rounds.Count);
    }

    /// <summary>What the command resolves from the server's container, over the app role and the leader just started.</summary>
    private static ServiceProvider SpotLoadServices(NpgsqlDataSource dataSource, Leader leader) {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(leader);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAnonTagger>(new AnonTagger(RandomNumberGenerator.GetBytes(AnonTagger.KeySize)));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<CursorRepo>();
        services.AddSingleton<MembershipCache>();
        services.AddSingleton<PokerStore>();
        services.AddSingleton<TeamStore>();
        return services.BuildServiceProvider();
    }

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
