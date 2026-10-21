using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Hosting;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

// The RUNBOOK's key rotation: a start with a new first key in APP_KEYS re-seals the stored Jira credentials.
[Trait("Category", "JiraClient")]
[Collection(SharedPostgres.Name)]
public sealed class CredentialRotationTests(PostgresFixture postgres) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_leader_reseals_old_boxes_under_the_new_first_key_at_start() {
        var oldKeys = JiraHarness.NewKeys("k1");
        await using var h = await JiraHarness.StartAsync(postgres, appKeys: oldKeys, ownDatabase: true);
        var appKeys = $"{JiraHarness.NewKeys("k2")},{oldKeys}";
        var credentials = new Credentials(Keyring.Parse(appKeys), h.DataSource, h.Time);

        await RunAsync(credentials, appKeys, isLeader: true);

        var after = await credentials.LoadAsync(h.Team, Ct);
        Assert.Equal("k2", after!.KeyId);
        Assert.Equal(h.Jira.BasicAuthorization, "Basic " + credentials.TryCreateBasicParameter(h.Team, after.CredentialsBox));
    }

    [Fact]
    public async Task A_follower_leaves_the_boxes_to_the_leader() {
        var oldKeys = JiraHarness.NewKeys("k1");
        await using var h = await JiraHarness.StartAsync(postgres, appKeys: oldKeys, ownDatabase: true);
        var appKeys = $"{JiraHarness.NewKeys("k2")},{oldKeys}";
        var credentials = new Credentials(Keyring.Parse(appKeys), h.DataSource, h.Time);

        await RunAsync(credentials, appKeys, isLeader: false);

        Assert.Equal("k1", (await credentials.LoadAsync(h.Team, Ct))!.KeyId);
    }

    [Fact]
    public async Task A_single_key_keyring_has_nothing_to_reseal() {
        var keys = JiraHarness.NewKeys("k1");
        await using var h = await JiraHarness.StartAsync(postgres, appKeys: keys, ownDatabase: true);
        var credentials = new Credentials(Keyring.Parse($"{JiraHarness.NewKeys("k2")},{keys}"), h.DataSource, h.Time);

        await RunAsync(credentials, JiraHarness.NewKeys("k2"), isLeader: true);

        Assert.Equal("k1", (await credentials.LoadAsync(h.Team, Ct))!.KeyId);
    }

    private static async Task RunAsync(Credentials credentials, string appKeys, bool isLeader) {
        await using var services = new ServiceCollection()
            .AddSingleton(credentials)
            .AddSingleton<ILeaderEpoch>(new FakeLeader(isLeader))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("APP_KEYS", appKeys)]).Build();
        using var rotation = new CredentialRotation(services, configuration, NullLogger<CredentialRotation>.Instance);
        await rotation.StartAsync(Ct);
        await rotation.ExecuteTask!;
    }
}
