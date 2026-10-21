using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;
using static Retroscope.Integration.Tests.Actors.ActorHarness;
using static Retroscope.Integration.Tests.TeamRoom.TeamRoomScope;
using TeamRoomModule = Retroscope.Server.Realtime.Rooms.TeamRoom;

namespace Retroscope.Integration.Tests.TeamRoom;

[Trait("Category", "TeamRoom")]
[Collection(SharedPostgres.Name)]
public sealed class MembershipCacheTests(PostgresFixture postgres) {
    [Fact]
    public async Task Load_reads_the_rows_once_and_role_lookups_then_need_no_io() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Admin);

        var loaded = await scope.Cache.LoadAsync(Team, Token);
        await scope.ExecAsync("delete from team_members where team_id = $1", Team.Value);

        Assert.Equal(TeamRole.Admin, loaded.RoleOf(Alice.Who.Id));
        Assert.Equal(TeamRole.Admin, scope.Cache.RoleIn(Team, Alice.Who.Id));
        Assert.Same(loaded, await scope.Cache.LoadAsync(Team, Token));
    }

    [Fact]
    public async Task A_lookup_before_any_load_reads_the_rows() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);

        Assert.Equal(TeamRole.Member, scope.Cache.RoleIn(Team, Alice.Who.Id));
        Assert.Null(scope.Cache.RoleIn(Team, Bob.Who.Id));
        Assert.True(scope.Cache.TryGet(Team, out _));
    }

    [Fact]
    public async Task A_slow_load_never_replaces_a_published_version() {
        await using var scope = await StartAsync(postgres);
        var published = scope.Cache.Publish(Team, new Dictionary<PrincipalId, TeamRole> { [Alice.Who.Id] = TeamRole.Owner });

        var seeded = scope.Cache.Seed(Team, new Dictionary<PrincipalId, TeamRole>());
        var next = scope.Cache.Publish(Team, new Dictionary<PrincipalId, TeamRole>());

        Assert.Same(published, seeded);
        Assert.Equal(published.Version + 1, next.Version);
        Assert.Null(scope.Cache.RoleIn(Team, Alice.Who.Id));
    }

    [Fact]
    public async Task A_grant_counts_only_for_the_current_password() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice);
        var first = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(1);
        await scope.ExecAsync("insert into board_access_grants (board_id, principal_id, password_epoch) values ($1, $2, $3)", Board.Value, Alice.Who.Id.Value, first);

        Assert.True(scope.Cache.HasCurrentGrant(Board, Alice.Who.Id, first));
        Assert.False(scope.Cache.HasCurrentGrant(Board, Alice.Who.Id, second));
        Assert.False(scope.Cache.HasCurrentGrant(Board, Alice.Who.Id, null));
        Assert.False(scope.Cache.HasCurrentGrant(Board, Bob.Who.Id, first));

        await scope.ExecAsync("update board_access_grants set password_epoch = $3 where board_id = $1 and principal_id = $2", Board.Value, Alice.Who.Id.Value, second);
        Assert.True(scope.Cache.HasCurrentGrant(Board, Alice.Who.Id, second));
    }

    [Fact]
    public void The_module_registers_the_cache_as_the_access_view_and_the_membership_lookup() {
        var services = new ServiceCollection();
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=localhost;Username=nobody"));
        TeamRoomModule.AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<MembershipCache>();
        Assert.Same(cache, provider.GetRequiredService<IAccessView>());
        Assert.Same(cache, provider.GetRequiredService<IMembershipLookup>());
        Assert.NotNull(provider.GetRequiredService<ITeamNotices>());
    }
}
