using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
[Collection(SharedPostgres.Name)]
public sealed class HealthTests(PostgresFixture postgres) {
    [Fact]
    public async Task Healthy_when_leader_and_the_database_answers() {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await StartAsync(database.AppConnectionString, new FakeLeader(true));

        using var response = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unhealthy_while_not_leader() {
        var database = await postgres.CreateDatabaseAsync();
        var leader = new FakeLeader(false);
        await using var app = await StartAsync(database.AppConnectionString, leader);

        using var before = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);
        leader.IsLeader = true;
        using var after = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(before, 503, "unavailable");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Unhealthy_without_a_lease_holder() {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await StartAsync(database.AppConnectionString, leader: null);

        using var response = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 503, "unavailable");
    }

    [Fact]
    public async Task Unhealthy_when_the_database_does_not_answer() {
        var unreachable = "Host=127.0.0.1;Port=1;Database=retroscope;Username=retroscope_app;Password=x;Timeout=1";
        await using var app = await StartAsync(unreachable, new FakeLeader(true));

        using var response = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 503, "unavailable");
    }

    [Fact]
    public async Task Unhealthy_once_shutdown_begins() {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await StartAsync(database.AppConnectionString, new FakeLeader(true));

        await app.Services.GetRequiredService<ShutdownCoordinator>().RunAsync(TestContext.Current.CancellationToken);
        using var response = await app.Client.GetAsync(new Uri(Health.Path, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 503, "unavailable");
    }

    [Fact]
    public async Task Kestrel_and_host_limits_match_the_design() {
        await using var app = await TestApp.StartAsync();

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(2000, kestrel.Limits.MaxConcurrentConnections);
        Assert.Equal(2 * 1024 * 1024, kestrel.Limits.MaxRequestBodySize);
        Assert.Equal(TimeSpan.FromMinutes(2), kestrel.Limits.KeepAliveTimeout);
        Assert.False(kestrel.AddServerHeader);
        Assert.Equal(TimeSpan.FromSeconds(7), app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    private static Task<TestApp> StartAsync(string appConnectionString, FakeLeader? leader) =>
        TestApp.StartAsync(services: services => {
            services.AddSingleton(_ => Db.CreateDataSource(appConnectionString));
            if (leader is not null) {
                services.AddSingleton<ILeaderEpoch>(leader);
            }
        });
}
