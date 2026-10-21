using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
[Collection(ImporterPostgres.Name)]
public sealed class SnapshotTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_table_is_read_from_the_same_instant() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, """
            set session_replication_role = replica;
            insert into public.teams (id, name) values ('00000000-0000-0000-0000-00000000000a', 'Before');
            """);
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(Ct);
        var layout = await Preflight.RunAsync(connection, ExpectedSchema.Embedded, new ImportReport(), Ct);

        await using var snapshot = await SourceSnapshot.BeginAsync(connection, layout, Ct);
        var countBefore = await snapshot.CountAsync("public.teams", Ct);
        await ImporterDatabases.ExecuteAsync(source, """
            set session_replication_role = replica;
            insert into public.teams (id, name) values ('00000000-0000-0000-0000-00000000000b', 'After');
            insert into public.team_members (team_id, user_id, role)
              select '00000000-0000-0000-0000-00000000000b', gen_random_uuid(), 'owner';
            """);
        var teams = await snapshot.ReadAsync("public.teams", Ct);
        var members = await snapshot.ReadAsync("public.team_members", Ct);

        Assert.Equal(1L, countBefore);
        Assert.Equal(["Before"], teams.Select(x => x.Text("name")));
        Assert.Empty(members);
        Assert.Equal(2L, await ImporterDatabases.ScalarAsync<long>(source, "select count(*) from public.teams"));
    }

    [Fact]
    public async Task The_snapshot_is_read_only() {
        var source = await databases.CreateSourceAsync();
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(Ct);
        var layout = await Preflight.RunAsync(connection, ExpectedSchema.Embedded, new ImportReport(), Ct);
        await using var snapshot = await SourceSnapshot.BeginAsync(connection, layout, Ct);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => snapshot.ExecuteAsync("insert into public.teams (id, name) values (gen_random_uuid(), 'x')", Ct));

        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, failure.SqlState);
    }

    [Fact]
    public async Task Values_keep_their_types() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, """
            set session_replication_role = replica;
            insert into public.poker_sessions (id, room_id, observer_ids, last_activity_at)
              values ('00000000-0000-0000-0000-0000000000c1', 'ROOM', '{00000000-0000-0000-0000-0000000000d1}', '2026-01-02T03:04:05.123456Z');
            insert into public.poker_session_rounds (session_id, round_number, selections, average_points, game_state)
              values ('00000000-0000-0000-0000-0000000000c1', 1, '{"u": {"points": 3}}', 2.5, 'Playing');
            insert into public.organizations (id, name, slug) values ('00000000-0000-0000-0000-0000000000e1', 'Org', 'org');
            insert into public.organization_members (organization_id, user_id, role)
              values ('00000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-0000000000d1', 'admin');
            """);
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(Ct);
        var layout = await Preflight.RunAsync(connection, ExpectedSchema.Embedded, new ImportReport(), Ct);
        await using var snapshot = await SourceSnapshot.BeginAsync(connection, layout, Ct);

        var session = Assert.Single(await snapshot.ReadAsync("public.poker_sessions", Ct));
        var round = Assert.Single(await snapshot.ReadAsync("public.poker_session_rounds", Ct));
        var member = Assert.Single(await snapshot.ReadAsync("public.organization_members", Ct));

        Assert.Equal([new Guid("00000000-0000-0000-0000-0000000000d1")], session.Get<Guid[]>("observer_ids")!);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560), session.Timestamp("last_activity_at"));
        Assert.Equal(DateTimeKind.Utc, session.Timestamp("last_activity_at")!.Value.Kind);
        Assert.Equal(2.5m, round.Get<decimal>("average_points"));
        Assert.Equal("""{"u": {"points": 3}}""", round.Text("selections"));
        Assert.False(round.Has("ticket_title"));
        Assert.Equal("admin", member.Text("role"));
    }

    [Fact]
    public async Task Excluded_tables_cannot_be_read() {
        var source = await databases.CreateSourceAsync();
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(Ct);
        var layout = await Preflight.RunAsync(connection, ExpectedSchema.Embedded, new ImportReport(), Ct);
        await using var snapshot = await SourceSnapshot.BeginAsync(connection, layout, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ReadAsync("public.consultation_requests", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ReadAsync("auth.refresh_tokens", Ct));
    }
}
