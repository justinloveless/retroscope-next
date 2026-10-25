using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Endpoints;

/// <summary>A new team session takes no options; the body is empty or <c>{}</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewSessionRequest;

public sealed record RoomCreated(Guid Id, string Code);

/// <summary>
/// The answer of <c>by-code</c>: the same two members for every found room, never the team (S9). Missing and
/// forbidden rooms get <see cref="RoomResolver.HiddenResult"/> instead.
/// </summary>
/// <param name="Access">Always <c>granted</c>: a room is found only for a member of its team (UX18).</param>
public sealed record RoomLookup(Guid Id, string Access) {
    public const string Granted = "granted";
}

/// <param name="NextCursor">Written as <c>null</c> on the last page.</param>
public sealed record ListPage<T>(IReadOnlyList<T> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

public static class ListPage {
    public static ListPage<T> Of<T>(Page<T> page) => new(page.Items, page.Next?.ToString());
}

/// <summary>An HTTP caller as the actors see it: a request has no tab or socket, so both ids are fresh.</summary>
public static class HttpViewer {
    public static Viewer Of(Principal principal) =>
        new(principal, new ClientId(Guid.NewGuid()), new ConnectionId(Guid.NewGuid()));
}

/// <summary>Turns an actor's answer to an HTTP ask into a response.</summary>
public static class RoomAsk {
    public static IResult Result(AskResult result, Func<IResult> done) => result.Status switch {
        AskStatus.Accepted or AskStatus.NoOp => done(),
        AskStatus.NotFound => Problem.Result(StatusCodes.Status404NotFound, "not_found"),
        AskStatus.Rejected => Rejected(result.Code ?? NackCode.Invalid),
        _ => Unavailable(),
    };

    /// <summary>The room is starting, stopping or could not commit; the client may retry (§d retry rule).</summary>
    public static IResult Unavailable() => new UnavailableResult();

    private static IResult Rejected(NackCode code) {
        var status = code switch {
            NackCode.Forbidden or NackCode.BoardArchived or NackCode.StageForbids => StatusCodes.Status403Forbidden,
            NackCode.NotFound => StatusCodes.Status404NotFound,
            NackCode.TooLarge => StatusCodes.Status413PayloadTooLarge,
            NackCode.Internal => StatusCodes.Status500InternalServerError,
            NackCode.ServerBusy or NackCode.RateLimited => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem.Result(status, JsonSerializer.Serialize(code, WireJsonContext.Default.NackCode).Trim('"'));
    }

    private sealed class UnavailableResult : IResult {
        public Task ExecuteAsync(HttpContext httpContext) {
            httpContext.Response.Headers.RetryAfter = "1";
            return Problem.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, "server_busy");
        }
    }
}

/// <summary>Team routes need the caller to be a member of a team that is not deleted; anyone else gets 404.</summary>
public static class TeamGate {
    public static async Task<TeamRole?> RoleAsync(HttpContext context, NpgsqlDataSource dataSource, Guid team) {
        var caller = (await context.GetCallerAsync())!;
        return await TeamRoles.RoleAsync(dataSource, new TeamId(team), caller.Principal.Id, context.RequestAborted);
    }

    public static IResult NotMember() => Problem.Result(StatusCodes.Status404NotFound, "not_found");
}

/// <summary>
/// Creates rooms with a fresh code (S9: 10 Crockford characters), retrying the rare collision. A code taken in another
/// letter case counts as taken, because lookups ignore case.
/// </summary>
public static class RoomCodes {
    private const int Attempts = 5;

    /// <returns>The code used, or null when every attempt collided.</returns>
    public static async Task<RoomCode?> CreateAsync(Func<RoomCode, Task<bool>> insert) {
        for (var attempt = 0; attempt < Attempts; attempt++) {
            var code = RoomCode.NewRandom();
            try {
                if (await insert(code)) {
                    return code;
                }
            } catch (PostgresException e) when (RetroListsRepo.IsCodeCollision(e)) {
                // A concurrent insert took the same code between the check and the insert.
            }
        }
        return null;
    }
}

/// <summary>
/// The room snapshot in the boot JSON of <c>/poker/:code</c>, <c>/teams/:tid/poker/:code</c> and <c>/retro/:code</c>
/// (§c.3, §g.1): <c>room = {r, access, …the viewer's snap frame}</c>. Lookups go through <see cref="RoomResolver"/>, so
/// a missing or forbidden room adds nothing. The snapshot is best effort: when the actor does not answer within
/// <see cref="SnapshotBudget"/> the page gets <c>{r, access}</c> and the socket delivers the snapshot.
/// </summary>
public sealed class RoomBootContributor(RoomResolver resolver, ActorRegistry registry) : IBootContributor {
    public static readonly TimeSpan SnapshotBudget = TimeSpan.FromMilliseconds(400);

    public async ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
        RoomKind? kind = boot.Route.Pattern switch {
            "/poker/:code" or "/teams/:tid/poker/:code" => RoomKind.Poker,
            "/retro/:code" => RoomKind.Board,
            _ => null,
        };
        if (kind is null || !boot.Parameters.TryGetValue("code", out var code)) {
            return;
        }
        // Only a member is ever admitted, so a caller who is not signed in spends no lookup (UX18).
        if (await boot.Http.GetCallerAsync() is not { IsUser: true } caller) {
            return;
        }
        var resolution = await resolver.ResolveAsync(boot.Http, kind.Value, code, cancellationToken);
        if (resolution is not RoomResolution.Found found) {
            return;
        }
        if (boot.Parameters.TryGetValue("tid", out var tid)
            && (!Guid.TryParse(tid, out var team) || found.Facts.Team.Value != team)) {
            return;
        }
        var snap = await SnapAsync(found.Room, HttpViewer.Of(caller.Principal), cancellationToken);
        boot.Set("room", Compose(found.Room, snap));
    }

    private async Task<byte[]?> SnapAsync(RoomKey room, Viewer viewer, CancellationToken cancellationToken) {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SnapshotBudget);
        try {
            return room.Kind == RoomKind.Poker
                ? await registry.QueryAsync<PokerState, PokerInput, byte[]?>(room, view => Snap(view, viewer), budget.Token)
                : await registry.QueryAsync<RetroState, RetroInput, byte[]?>(room, view => Snap(view, viewer), budget.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return null;
        } catch (RoomUnavailableException) {
            return null;
        }
    }

    // A view of a room deleted since the lookup is not found; SnapFrameAsync would throw on it.
    private static async ValueTask<byte[]?> Snap<TState>(RoomView<TState> view, Viewer viewer) where TState : class {
        return view.CanSubscribe(viewer) ? await view.SnapFrameAsync(viewer) : null;
    }

    private static JsonElement Compose(RoomKey room, byte[]? snap) {
        var buffer = new ArrayBufferWriter<byte>(snap?.Length + 128 ?? 128);
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("r", room.ToString());
            writer.WriteString("access", RoomLookup.Granted);
            if (snap is not null) {
                using var frame = JsonDocument.Parse(snap);
                foreach (var member in frame.RootElement.EnumerateObject()) {
                    if (member.Name is not ("r" or "access")) {
                        member.WriteTo(writer);
                    }
                }
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Team poker sessions over HTTP (§d): look up by code, delete, and the team's list and session creation. Every
/// session belongs to a team and opens to its members only (UX18).
/// </summary>
public static class PokerEndpoints {
    public static readonly RateLimit Manage = RateLimit.Define(
        "poker.manage", RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)), RateRule.PerIp(60, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit TeamRead = RateLimit.Define(
        "team.rooms.read", RateRule.PerPrincipal(300, TimeSpan.FromMinutes(1)), RateRule.PerIp(600, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit TeamCreate = RateLimit.Define(
        "team.rooms.create", RateRule.PerPrincipal(60, TimeSpan.FromHours(1)), RateRule.PerIp(120, TimeSpan.FromHours(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<PokerListsRepo>();
        services.AddSingleton<IBootContributor, RoomBootContributor>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        // RoomResolver limits every code lookup itself (30/min per IP, S9), whichever route makes it. Only a member can
        // be admitted, so a caller who is not signed in is refused before any lookup is spent (UX18).
        app.MapGet("/api/poker/by-code/{code}", ByCodeAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(RateLimit.Unlimited);
        app.MapDelete("/api/poker/{id:guid}", DeleteAsync)
            .RequireRoutePolicy(RoutePolicy.SessionManage)
            .RequireRateLimit(Manage);
        app.MapGet("/api/teams/{id:guid}/poker-sessions", ListTeamAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(TeamRead);
        app.MapPost("/api/teams/{id:guid}/poker-sessions", CreateTeamAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(TeamCreate);
    }

    private static async Task<IResult> ByCodeAsync(HttpContext context, string code, RoomResolver resolver) {
        var resolution = await resolver.ResolveAsync(context, RoomKind.Poker, code, context.RequestAborted);
        return resolution is RoomResolution.Found found
            ? Results.Json(new RoomLookup(found.Room.Id, RoomLookup.Granted), RoomHttpJsonContext.Default.RoomLookup)
            : RoomResolver.Result(resolution);
    }

    // §d: Ask → poker.session.deleted → the actor kicks every subscriber (`deleted`) → the store deletes the rows.
    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        Guid id,
        PokerListsRepo repo,
        ActorRegistry registry,
        IServiceProvider services) {
        var session = new SessionId(id);
        if (await repo.OwnerAsync(session, context.RequestAborted) is not { } owner) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var caller = (await context.GetCallerAsync())!;
        var result = await registry.AskAsync<PokerState, PokerInput>(
            RoomKey.Poker(session), new DeleteSession(), HttpViewer.Of(caller.Principal), context.RequestAborted);
        if (result.Status == AskStatus.Accepted && owner.Team is { } team) {
            // A total the list does not hold makes the team page reload it, and the session is gone from it.
            services.GetService<ITeamNotices>()?.PokerChanged(team, session, 0, 0);
        }
        return RoomAsk.Result(result, Results.NoContent);
    }

    private static async Task<IResult> ListTeamAsync(
        HttpContext context,
        Guid id,
        string? cursor,
        int? limit,
        PokerListsRepo repo,
        NpgsqlDataSource dataSource) {
        if (!ListCursor.TryParse(cursor, out var after)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["cursor"] = "invalid" });
        }
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        var page = await repo.ListTeamSessionsAsync(new TeamId(id), after, ListCursor.Limit(limit), context.RequestAborted);
        return Results.Json(ListPage.Of(page), RoomHttpJsonContext.Default.ListPagePokerSessionListItem);
    }

    private static async Task<IResult> CreateTeamAsync(
        HttpContext context,
        Guid id,
        NewSessionRequest? body,
        PokerListsRepo repo,
        NpgsqlDataSource dataSource) {
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        var caller = (await context.GetCallerAsync())!;
        var notices = context.RequestServices.GetService<ITeamNotices>();
        return await CreateAsync(repo, new TeamId(id), caller.Principal.Id, notices, context.RequestAborted);
    }

    private static async Task<IResult> CreateAsync(
        PokerListsRepo repo,
        TeamId team,
        PrincipalId createdBy,
        ITeamNotices? notices,
        CancellationToken cancellationToken) {
        var session = new SessionId(Guid.CreateVersion7());
        var round = new RoundId(Guid.CreateVersion7());
        var code = await RoomCodes.CreateAsync(x => repo.CreateSessionAsync(session, round, x, team, createdBy, cancellationToken));
        if (code is not { } created) {
            return RoomAsk.Unavailable();
        }
        notices?.PokerChanged(team, session, 0, 1);
        return Results.Json(
            new RoomCreated(session.Value, created.Value),
            RoomHttpJsonContext.Default.RoomCreated,
            statusCode: StatusCodes.Status201Created);
    }
}
