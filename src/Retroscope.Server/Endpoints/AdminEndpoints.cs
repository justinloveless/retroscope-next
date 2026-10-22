using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Auth;
using Retroscope.Server.Auth.Email;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jobs;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Server.Endpoints;

/// <param name="Email">Masked: <c>a•••@example.com</c>.</param>
public sealed record AdminUser(PrincipalId Id, string Name, string Email, bool IsAdmin, bool Disabled);

public sealed record AdminTeam(TeamId Id, string Name, int MemberCount);

/// <summary>A flat object the admin page shows as a key/value list.</summary>
public sealed record AdminStats(
    long UptimeSeconds,
    long RssBytes,
    long ManagedHeapBytes,
    int LiveRooms,
    int Sockets,
    int PoolSize,
    int PoolOpen,
    int PoolInUse,
    long RingBytes,
    long BlobBytes,
    long EmailBudgetDrops,
    int OutboxDepth);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminUserPatch(bool? Disabled, bool? IsAdmin);

/// <param name="Role">
/// Defaults to <c>member</c>. Making someone the owner transfers ownership: the current owner becomes an admin.
/// </param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminMemberRequest(PrincipalId? UserId, TeamRole? Role);

/// <summary>
/// The app-admin API (§d, <c>X</c>): user search, disable and admin flags with last-admin protection, team search,
/// membership repair through the team actor, and process stats. Every change is audited. Also registers the
/// retention jobs (U0 decision 2: <c>Jobs/</c> has no module of its own).
/// </summary>
public static class AdminEndpoints {
    public const int SearchLimit = 50;

    /// <summary>Serializes admin-flag changes, so two admins demoting each other cannot both succeed.</summary>
    public const long AdminFlagsLockKey = 0x52534144;

    public static readonly RateLimit Read = RateLimit.Define(
        "admin.read", RateRule.PerIp(120, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(120, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Write = RateLimit.Define(
        "admin.write", RateRule.PerIp(60, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(60, TimeSpan.FromMinutes(1)));

    // The roster name rule of the team store, so a member added here equals the same member loaded from the rows.
    private const string MemberNameSql = "coalesce(nullif(u.nickname, ''), nullif(u.full_name, ''), 'User')";

    private const string SearchUsersSql = $"""
        select u.id, {UsersRepo.DisplayNameSql}, u.email, u.is_admin, u.disabled_at is not null
        from users u
        where $1 = '' or u.email like $1 || '%' or lower(u.full_name) like $1 || '%' or lower(u.nickname) like $1 || '%'
        order by u.email
        limit $2
        """;

    private const string SearchTeamsSql = """
        select t.id, t.name, (select count(*) from team_members m where m.team_id = t.id)::int
        from teams t
        where t.deleted_at is null and ($1 = '' or strpos(lower(t.name), $1) > 0)
        order by lower(t.name), t.id
        limit $2
        """;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        AddJob<RetentionJob>(services);
        AddJob<TokenSweepJob>(services);
        AddJob<TeamPurgeJob>(services);
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/admin/users", SearchUsersAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Read);
        app.MapPatch("/api/admin/users/{id:guid}", PatchUserAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Write);
        app.MapGet("/api/admin/teams", SearchTeamsAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Read);
        app.MapPost("/api/admin/teams/{id:guid}/members", SetMemberAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Write);
        app.MapDelete("/api/admin/teams/{id:guid}/members/{userId:guid}", RemoveMemberAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Write);
        app.MapGet("/api/admin/stats", StatsAsync)
            .RequireRoutePolicy(RoutePolicy.AppAdmin)
            .RequireRateLimit(Read);
    }

    private static void AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(IServiceCollection services)
        where TJob : ScheduledJob {
        services.AddSingleton<TJob>();
        services.AddHostedService(sp => sp.GetRequiredService<TJob>());
    }

    private static async Task<IResult> SearchUsersAsync(string? q, HttpContext context, NpgsqlDataSource dataSource) {
        await using var command = dataSource.CreateCommand(SearchUsersSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = LikePrefix(q) });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = SearchLimit });
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var users = new List<AdminUser>();
        while (await reader.ReadAsync(context.RequestAborted)) {
            users.Add(new AdminUser(
                new PrincipalId(reader.GetGuid(0)),
                reader.GetString(1),
                EmailEndpoints.MaskEmail(reader.GetString(2)),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }
        return Results.Json(users, AdminJsonContext.Default.IReadOnlyListAdminUser);
    }

    // §d: "cannot demote the last admin". Disabling an admin removes their admin rights just as surely, so the rule
    // is that at least one enabled admin remains after the change.
    private static async Task<IResult> PatchUserAsync(
        Guid id,
        AdminUserPatch body,
        HttpContext context,
        NpgsqlDataSource dataSource,
        Sessions sessions,
        IEnumerable<IConnectionRevoker> revokers,
        TimeProvider time) {
        if (body.Disabled is null && body.IsAdmin is null) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid");
        }
        var cancellationToken = context.RequestAborted;
        var caller = (await context.GetCallerAsync())!;
        var user = new PrincipalId(id);
        var now = time.GetUtcNow();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "select pg_advisory_xact_lock($1)", cancellationToken, new NpgsqlParameter<long> { TypedValue = AdminFlagsLockKey });

        bool wasAdmin;
        bool wasDisabled;
        await using (var command = new NpgsqlCommand("select is_admin, disabled_at is not null from users where id = $1 for update", connection)) {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) {
                return Problem.Result(StatusCodes.Status404NotFound, "not_found");
            }
            wasAdmin = reader.GetBoolean(0);
            wasDisabled = reader.GetBoolean(1);
        }
        var isAdmin = body.IsAdmin ?? wasAdmin;
        var disabled = body.Disabled ?? wasDisabled;
        if (wasAdmin && !wasDisabled && !(isAdmin && !disabled)) {
            await using var others = new NpgsqlCommand(
                "select count(*) from users where is_admin and disabled_at is null and id <> $1", connection);
            others.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            if ((long)(await others.ExecuteScalarAsync(cancellationToken))! == 0) {
                return Problem.Result(StatusCodes.Status409Conflict, "last_admin");
            }
        }

        AdminUser result;
        await using (var update = new NpgsqlCommand($"""
            update users u set
              is_admin = $2,
              disabled_at = case when $3 then coalesce(u.disabled_at, $4) end,
              updated_at = $4
            where u.id = $1
            returning {UsersRepo.DisplayNameSql}, u.email
            """, connection)) {
            update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            update.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = isAdmin });
            update.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = disabled });
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            result = new AdminUser(user, reader.GetString(0), EmailEndpoints.MaskEmail(reader.GetString(1)), isAdmin, disabled);
        }

        await using (var audit = new NpgsqlBatch(connection, transaction)) {
            var target = user.ToString();
            if (disabled != wasDisabled) {
                audit.BatchCommands.Add(UsersRepo.Audit(
                    disabled ? "admin.user.disable" : "admin.user.enable", caller.Principal.Id, target, new Dictionary<string, string>(), now));
            }
            if (isAdmin != wasAdmin) {
                audit.BatchCommands.Add(UsersRepo.Audit(
                    isAdmin ? "admin.user.grant_admin" : "admin.user.revoke_admin", caller.Principal.Id, target, new Dictionary<string, string>(), now));
            }
            if (audit.BatchCommands.Count > 0) {
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(CancellationToken.None);

        // A disabled account must lose every session and socket at once; a privilege change takes effect on a fresh
        // sign-in, so cached sessions cannot keep the old rights.
        if ((disabled && !wasDisabled) || isAdmin != wasAdmin) {
            await sessions.RevokeAllAsync(user, keep: null, CancellationToken.None);
            foreach (var revoker in revokers) {
                revoker.RevokePrincipal(user);
            }
        }
        sessions.EvictUser(user);
        return Results.Json(result, AdminJsonContext.Default.AdminUser);
    }

    private static async Task<IResult> SearchTeamsAsync(string? q, HttpContext context, NpgsqlDataSource dataSource) {
        await using var command = dataSource.CreateCommand(SearchTeamsSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = (q ?? "").Trim().ToLowerInvariant() });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = SearchLimit });
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var teams = new List<AdminTeam>();
        while (await reader.ReadAsync(context.RequestAborted)) {
            teams.Add(new AdminTeam(new TeamId(reader.GetGuid(0)), reader.GetString(1), reader.GetInt32(2)));
        }
        return Results.Json(teams, AdminJsonContext.Default.IReadOnlyListAdminTeam);
    }

    // Membership repair goes through the team actor like every other membership change (R24), so the cache, the
    // roster and the team's live rooms follow. The owner can be replaced but never removed or demoted here, so a team
    // keeps exactly one owner.
    private static async Task<IResult> SetMemberAsync(
        Guid id,
        AdminMemberRequest body,
        HttpContext context,
        NpgsqlDataSource dataSource,
        ActorRegistry registry,
        TimeProvider time) {
        if (body.UserId is not { } userId) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["userId"] = "required" });
        }
        var cancellationToken = context.RequestAborted;
        var caller = (await context.GetCallerAsync())!;
        var team = new TeamId(id);
        var role = body.Role ?? TeamRole.Member;

        MemberInfo? user;
        await using (var command = dataSource.CreateCommand($"select {MemberNameSql}, u.avatar_blob_id from users u where u.id = $1")) {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = userId.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            user = await reader.ReadAsync(cancellationToken)
                ? new MemberInfo(userId, reader.GetString(0), role, reader.IsDBNull(1) ? null : new BlobId(reader.GetGuid(1)))
                : null;
        }
        if (user is null) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found", new Dictionary<string, string> { ["userId"] = "not_found" });
        }

        var members = await MembersAsync(registry, team, cancellationToken);
        if (members is null) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var current = members.GetValueOrDefault(userId);
        if (current?.Role == TeamRole.Owner && role != TeamRole.Owner) {
            return Problem.Result(StatusCodes.Status409Conflict, "owner_required");
        }

        if (role == TeamRole.Owner && members.Values.FirstOrDefault(x => x.Role == TeamRole.Owner) is { } owner && owner.Pid != userId) {
            return await ReplaceOwnerAsync(dataSource, registry, time, caller, team, owner.Pid, user, current is not null, cancellationToken);
        }

        TeamInput input = current is null ? new TeamInput.MemberAdded(user) : new TeamInput.MemberRoleSet(userId, role);
        var outcome = await registry.AskAsync<TeamState, TeamInput>(RoomKey.Team(team), input, null, cancellationToken);
        if (Refusal(outcome) is { } refusal) {
            return refusal;
        }
        if (outcome.Status == AskStatus.Accepted) {
            var action = current is null ? "admin.team.member.add" : "admin.team.member.role";
            await AuditTeamAsync(dataSource, action, caller, team, userId, role, time.GetUtcNow());
        }
        return Results.NoContent();
    }

    // TransferOwnership demotes the old owner and promotes the new one in one team actor transaction, so no failure can
    // leave the team without an owner. Someone who is not a member yet joins as a member first.
    private static async Task<IResult> ReplaceOwnerAsync(
        NpgsqlDataSource dataSource,
        ActorRegistry registry,
        TimeProvider time,
        Caller caller,
        TeamId team,
        PrincipalId owner,
        MemberInfo newOwner,
        bool isMember,
        CancellationToken cancellationToken) {
        if (!isMember) {
            var added = await registry.AskAsync<TeamState, TeamInput>(
                RoomKey.Team(team), new TeamInput.MemberAdded(newOwner with { Role = TeamRole.Member }), null, cancellationToken);
            if (Refusal(added) is { } refusal) {
                return refusal;
            }
            if (added.Status == AskStatus.Accepted) {
                await AuditTeamAsync(dataSource, "admin.team.member.add", caller, team, newOwner.Pid, TeamRole.Member, time.GetUtcNow());
            }
        }
        var outcome = await registry.AskAsync<TeamState, TeamInput>(
            RoomKey.Team(team), new TeamInput.TransferOwnership(owner, newOwner.Pid), null, cancellationToken);
        if (outcome is { Status: AskStatus.Rejected, Code: NackCode.Forbidden or NackCode.NotFound }) {
            // The owner changed, or the new owner left, since the roster was read.
            return Problem.Result(StatusCodes.Status409Conflict, "conflict");
        }
        if (Refusal(outcome) is { } failure) {
            return failure;
        }
        if (outcome.Status == AskStatus.Accepted) {
            await AuditTeamAsync(dataSource, "admin.team.owner.transfer", caller, team, newOwner.Pid, TeamRole.Owner, time.GetUtcNow());
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id,
        Guid userId,
        HttpContext context,
        NpgsqlDataSource dataSource,
        ActorRegistry registry,
        TimeProvider time) {
        var cancellationToken = context.RequestAborted;
        var caller = (await context.GetCallerAsync())!;
        var team = new TeamId(id);
        var user = new PrincipalId(userId);
        var members = await MembersAsync(registry, team, cancellationToken);
        if (members is null) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (members.GetValueOrDefault(user)?.Role == TeamRole.Owner) {
            return Problem.Result(StatusCodes.Status409Conflict, "owner_required");
        }
        var outcome = await registry.AskAsync<TeamState, TeamInput>(RoomKey.Team(team), new TeamInput.MemberRemoved(user), null, cancellationToken);
        if (Refusal(outcome) is { } refusal) {
            return refusal;
        }
        if (outcome.Status == AskStatus.Accepted) {
            await AuditTeamAsync(dataSource, "admin.team.member.remove", caller, team, user, null, time.GetUtcNow());
        }
        return Results.NoContent();
    }

    private static async Task<IResult> StatsAsync(
        HttpContext context,
        NpgsqlDataSource dataSource,
        ActorRegistry registry,
        ActorRuntime runtime,
        ConnectionRegistry connections,
        BlobsRepo blobs,
        EmailBudget budget) {
        var cancellationToken = context.RequestAborted;
        await using var batch = dataSource.CreateBatch();
        // The app role's backends in this database are the pool's connections; this query's own backend is busy.
        batch.BatchCommands.Add(new("""
            select count(*)::int, (count(*) filter (where state <> 'idle' and pid <> pg_backend_pid()))::int
            from pg_stat_activity where datname = current_database() and usename = current_user
            """));
        batch.BatchCommands.Add(new("select count(*)::int from email_outbox where body_text is not null"));
        int poolOpen;
        int poolInUse;
        int outbox;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken)) {
            await reader.ReadAsync(cancellationToken);
            poolOpen = reader.GetInt32(0);
            poolInUse = reader.GetInt32(1);
            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            outbox = reader.GetInt32(0);
        }
        using var process = Process.GetCurrentProcess();
        var stats = new AdminStats(
            (long)(DateTime.Now - process.StartTime).TotalSeconds,
            Environment.WorkingSet,
            GC.GetTotalMemory(forceFullCollection: false),
            registry.Count,
            connections.Count,
            new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize,
            poolOpen,
            poolInUse,
            runtime.RingBudget.TotalBytes,
            await blobs.BoardImageBytesAsync(cancellationToken),
            budget.Dropped,
            outbox);
        return Results.Json(stats, AdminJsonContext.Default.AdminStats);
    }

    /// <returns>The team's current members, or null when the team does not exist or was deleted.</returns>
    private static async Task<IReadOnlyDictionary<PrincipalId, MemberInfo>?> MembersAsync(
        ActorRegistry registry,
        TeamId team,
        CancellationToken cancellationToken) {
        return await registry.QueryAsync<TeamState, TeamInput, IReadOnlyDictionary<PrincipalId, MemberInfo>?>(
            RoomKey.Team(team),
            view => ValueTask.FromResult<IReadOnlyDictionary<PrincipalId, MemberInfo>?>(
                view.Found && !view.State!.Deleted ? new Dictionary<PrincipalId, MemberInfo>(view.State.Members) : null),
            cancellationToken);
    }

    private static IResult? Refusal(AskResult outcome) => outcome.Status switch {
        AskStatus.Accepted or AskStatus.NoOp => null,
        AskStatus.NotFound => Problem.Result(StatusCodes.Status404NotFound, "not_found"),
        AskStatus.Unavailable => Problem.Result(StatusCodes.Status503ServiceUnavailable, "unavailable"),
        _ => Problem.Result(StatusCodes.Status500InternalServerError, "internal"),
    };

    private static async Task AuditTeamAsync(
        NpgsqlDataSource dataSource,
        string action,
        Caller caller,
        TeamId team,
        PrincipalId target,
        TeamRole? role,
        DateTimeOffset now) {
        await using var command = dataSource.CreateCommand("""
            insert into audit_log (at, actor_id, team_id, action, target, meta) values ($6, $1, $2, $3, $4, $5)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = caller.Principal.Id.Value });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = team.Value });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.ToString() });
        command.Parameters.Add(new NpgsqlParameter<string> {
            TypedValue = role is { } value ? $$"""{"role":"{{RoleName(value)}}"}""" : "{}",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string RoleName(TeamRole role) => role switch {
        TeamRole.Owner => "owner",
        TeamRole.Admin => "admin",
        _ => "member",
    };

    /// <summary>The query as a lower-case <c>LIKE</c> prefix with its wildcards escaped; empty matches everything.</summary>
    private static string LikePrefix(string? q) {
        var text = new StringBuilder();
        foreach (var c in (q ?? "").Trim().ToLowerInvariant()) {
            if (c is '\\' or '%' or '_') {
                text.Append('\\');
            }
            text.Append(c);
        }
        return text.ToString();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters) {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
