using Microsoft.Extensions.Time.Testing;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>One test's database, clock, access view and server; <see cref="RestartAsync"/> is a new process on the same data.</summary>
internal sealed class PokerScope : IAsyncDisposable {
    private PokerScope(TestDatabase database, PokerSeed seed, TestAccess access, FakeJiraPort? jira, FakeTeamNotices? notices) {
        Database = database;
        Seed = seed;
        Access = access;
        Jira = jira;
        Notices = notices;
    }

    public TestDatabase Database { get; }

    public PokerSeed Seed { get; }

    public TestAccess Access { get; }

    public FakeJiraPort? Jira { get; }

    public FakeTeamNotices? Notices { get; }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public PokerHost Host { get; private set; } = null!;

    public static async Task<PokerScope> CreateAsync(PostgresFixture postgres, FakeJiraPort? jira = null, FakeTeamNotices? notices = null) {
        var database = await postgres.CreateDatabaseAsync();
        var access = new TestAccess();
        return new PokerScope(database, new PokerSeed(database, access), access, jira, notices);
    }

    public async Task<PokerHost> StartAsync() {
        Host = await PokerHost.StartAsync(Database, Time, Access, Jira, Notices);
        return Host;
    }

    public async Task<PokerHost> RestartAsync() {
        await Host.DisposeAsync();
        return await StartAsync();
    }

    public async ValueTask DisposeAsync() {
        if (Host is not null) {
            await Host.DisposeAsync();
        }
    }
}
