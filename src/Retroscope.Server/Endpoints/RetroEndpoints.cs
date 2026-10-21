using System.Text;
using System.Text.Json.Serialization;
using Npgsql;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;

namespace Retroscope.Server.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuickBoardRequest(string? Title);

/// <param name="Visibility">Defaults to <c>team</c> (§0.1: new team boards are team-only).</param>
/// <param name="TemplateId">One of the team's templates; the team's default template, else the built-in columns.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeamBoardRequest(string? Title, BoardVisibility? Visibility, string? Password, Guid? TemplateId);

/// <param name="Password">A new password; it implies <c>password</c> visibility and invalidates every grant.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BoardAccessRequest(BoardVisibility? Visibility, string? Password);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnlockRequest(string? Password);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteBoardRequest(string? ConfirmTitle);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionItemCommentRequest(CommentId? CommentId, string? Body);

/// <summary>A comment of an action item's source card: <see cref="CommentInfo"/> without the card.</summary>
public sealed record ActionItemComment(
    CommentId Id,
    string Body,
    bool Anonymous,
    long CreatedAt,
    AuthorInfo? Author = null,
    bool? Mine = null);

public sealed record ActionItemComments(IReadOnlyList<ActionItemComment> Items);

public sealed record ActionItemCommentPosted(CommentId CommentId);

/// <summary>
/// Quick and team boards over HTTP (§d): create, look up by code, unlock, access, archive, delete, the team's board
/// list, and the team page's action items with their comments.
/// </summary>
public static class RetroEndpoints {
    public const string QuickBoardTitle = "Quick Retro Board";
    public const int MaxTitleLength = 200;
    public const int MinPasswordBytes = 4;
    public const int MaxPasswordBytes = 72;

    /// <summary>S7: per principal and per IP, so fresh guest principals do not lift the limit.</summary>
    public static readonly RateLimit CreateQuick = RateLimit.Define(
        "retro.create", RateRule.PerPrincipal(10, TimeSpan.FromHours(1)), RateRule.PerIp(30, TimeSpan.FromHours(1)));

    /// <summary>§d: 20/h per IP; the per-principal-per-board half is <see cref="UnlockPerBoard"/>.</summary>
    public static readonly RateLimit Unlock = RateLimit.Define(
        "retro.unlock", RateRule.PerIp(20, TimeSpan.FromHours(1)), RateRule.PerPrincipal(60, TimeSpan.FromHours(1)));

    /// <summary>§d: 5/min per principal per board; the subject is the pair.</summary>
    public static readonly RateLimit UnlockPerBoard = RateLimit.Define(
        "retro.unlock.board", RateRule.PerSubject(5, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Manage = RateLimit.Define(
        "retro.manage", RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)), RateRule.PerIp(60, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit CommentPost = RateLimit.Define(
        "team.action.comment", RateRule.PerPrincipal(60, TimeSpan.FromMinutes(1)), RateRule.PerIp(120, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<RetroListsRepo>();
        services.AddSingleton<ActionItemCommentsRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/api/retro", CreateQuickAsync)
            .RequireRoutePolicy(RoutePolicy.Guest)
            .RequireRateLimit(CreateQuick);
        // RoomResolver limits every code lookup itself (30/min per IP, S9), whichever route makes it.
        app.MapGet("/api/retro/by-code/{code}", ByCodeAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited);
        app.MapPost("/api/retro/{id:guid}/unlock", UnlockAsync)
            .RequireRoutePolicy(RoutePolicy.Guest)
            .RequireRateLimit(Unlock);
        app.MapPatch("/api/retro/{id:guid}", SetAccessAsync)
            .RequireRoutePolicy(RoutePolicy.BoardManage)
            .RequireRateLimit(Manage);
        app.MapPost("/api/retro/{id:guid}/archive", (HttpContext context, Guid id, RetroListsRepo repo, ActorRegistry registry) =>
                SetArchivedAsync(context, id, true, repo, registry))
            .RequireRoutePolicy(RoutePolicy.BoardManage)
            .RequireRateLimit(Manage);
        app.MapPost("/api/retro/{id:guid}/unarchive", (HttpContext context, Guid id, RetroListsRepo repo, ActorRegistry registry) =>
                SetArchivedAsync(context, id, false, repo, registry))
            .RequireRoutePolicy(RoutePolicy.BoardManage)
            .RequireRateLimit(Manage);
        app.MapDelete("/api/retro/{id:guid}", DeleteAsync)
            .RequireRoutePolicy(RoutePolicy.BoardManage)
            .RequireRateLimit(Manage);

        app.MapGet("/api/teams/{id:guid}/boards", ListTeamAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(PokerEndpoints.TeamRead);
        app.MapPost("/api/teams/{id:guid}/boards", CreateTeamAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(PokerEndpoints.TeamCreate);
        app.MapGet("/api/teams/{id:guid}/action-items", ListActionItemsAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(PokerEndpoints.TeamRead);
        app.MapGet("/api/teams/{id:guid}/action-items/{itemId:guid}/comments", ListCommentsAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(PokerEndpoints.TeamRead);
        app.MapPost("/api/teams/{id:guid}/action-items/{itemId:guid}/comments", PostCommentAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(CommentPost);
    }

    private static async Task<IResult> CreateQuickAsync(HttpContext context, QuickBoardRequest? body, RetroListsRepo repo) {
        if (!TryTitle(body?.Title, QuickBoardTitle, out var title)) {
            return Invalid("title");
        }
        var caller = (await context.GetCallerAsync())!;
        var template = new BoardTemplate(RetroListsRepo.DefaultSettings with { MaxVotes = 3 }, RetroListsRepo.DefaultColumns);
        return await CreateAsync(repo, null, title, caller.Principal.Id, BoardVisibility.Link, null, template, null, context.RequestAborted);
    }

    private static async Task<IResult> ByCodeAsync(HttpContext context, string code, RoomResolver resolver) {
        var resolution = await resolver.ResolveAsync(context, RoomKind.Board, code, context.RequestAborted);
        if (resolution is not RoomResolution.Found found) {
            return RoomResolver.Result(resolution);
        }
        var access = found.Access == RoomAccess.Granted ? "granted" : "password";
        return Results.Json(new RoomLookup(found.Room.Id, access), RoomHttpJsonContext.Default.RoomLookup);
    }

    // Anyone who can reach a password board may try its password; every other board answers as the resolver does.
    private static async Task<IResult> UnlockAsync(
        HttpContext context,
        Guid id,
        UnlockRequest body,
        RetroListsRepo repo,
        RateLimiter limiter,
        HashGate gate) {
        var who = (await context.GetCallerAsync())!.Principal;
        var keys = new RateKeys(ClientIp.LimitKey(context), who.Id.ToString(), $"{id:N}:{who.Id}");
        if (!limiter.TryAcquire(UnlockPerBoard, keys, out var retryAfter)) {
            return RateLimits.LimitedResult(retryAfter);
        }
        var cancellationToken = context.RequestAborted;
        var board = await repo.BoardAsync(new BoardId(id), who.Id, cancellationToken);
        if (board is null || board.Facts.Deleted || board.Facts.TeamDeleted) {
            return RoomResolver.HiddenResult();
        }
        if (Policy.Can(who, Capability.Subscribe, board.Facts, board.Access)) {
            return Results.NoContent();
        }
        if (board.Facts.Visibility != BoardVisibility.Password || board.PasswordHash is not { } hash || board.Facts.PasswordSetAt is not { } epoch) {
            return RoomResolver.HiddenResult();
        }
        var gated = await gate.RunAsync(() => Bcrypt.Verify(body.Password ?? "", hash), cancellationToken);
        if (!gated.Admitted) {
            return HashGate.BusyResult();
        }
        if (!gated.Value) {
            return Problem.Result(StatusCodes.Status403Forbidden, "wrong_password");
        }
        // The membership cache reads a grant it does not hold, so the next subscribe sees this one (U23).
        return await repo.GrantAsync(new BoardId(id), who.Id, epoch, cancellationToken)
            ? Results.NoContent()
            : Problem.Result(StatusCodes.Status409Conflict, "password_changed");
    }

    /// <summary>
    /// §d: the route writes visibility and password itself (the board cannot, U22 decision 4), then the live board
    /// learns the change, then every subscriber is re-checked: a new password drops every grant, so holders of the
    /// old one are kicked.
    /// </summary>
    private static async Task<IResult> SetAccessAsync(
        HttpContext context,
        Guid id,
        BoardAccessRequest body,
        RetroListsRepo repo,
        ActorRegistry registry,
        HashGate gate) {
        var cancellationToken = context.RequestAborted;
        var who = (await context.GetCallerAsync())!.Principal;
        var boardId = new BoardId(id);
        if (await ManagedAsync(repo, boardId, who, Capability.RetroManageAccess, cancellationToken) is not { } board) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (!Policy.Can(who, Capability.RetroManageAccess, board.Facts, board.Access)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
        }
        var visibility = body.Visibility ?? (body.Password is not null ? BoardVisibility.Password : board.Facts.Visibility!.Value);
        if (visibility == BoardVisibility.Team && board.Facts.Team is null) {
            return Invalid("visibility");
        }
        if (body.Password is not null && (visibility != BoardVisibility.Password || !ValidPassword(body.Password))) {
            return Invalid("password");
        }
        if (visibility == BoardVisibility.Password && body.Password is null && board.PasswordHash is null) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["password"] = "required" });
        }
        if (visibility == board.Facts.Visibility && body.Password is null) {
            return Results.NoContent();
        }

        string? hash = null;
        if (body.Password is { } password) {
            var gated = await gate.RunAsync(() => Bcrypt.Hash(password), cancellationToken);
            if (!gated.Admitted) {
                return HashGate.BusyResult();
            }
            hash = gated.Value;
        }
        var (found, passwordSetAt) = await repo.SetAccessAsync(boardId, visibility, hash, cancellationToken);
        if (!found) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var room = RoomKey.Board(boardId);
        var result = await registry.AskAsync<RetroState, RetroInput>(
            room, new RetroInput.AccessSet(visibility, passwordSetAt), HttpViewer.Of(who), cancellationToken);
        registry.AccessChanged(room);
        // The rows are written; a board that could not be told now loads them when it next starts.
        return result.Status == AskStatus.Unavailable ? RoomAsk.Unavailable() : Results.NoContent();
    }

    private static async Task<IResult> SetArchivedAsync(
        HttpContext context,
        Guid id,
        bool archived,
        RetroListsRepo repo,
        ActorRegistry registry) {
        var who = (await context.GetCallerAsync())!.Principal;
        var boardId = new BoardId(id);
        if (await ManagedAsync(repo, boardId, who, Capability.RetroArchive, context.RequestAborted) is not { } board) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (!Policy.Can(who, Capability.RetroArchive, board.Facts, board.Access)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
        }
        // The board persists the archive flag and posts BoardsChanged to its team (U22).
        var result = await registry.AskAsync<RetroState, RetroInput>(
            RoomKey.Board(boardId), new RetroInput.ArchiveSet(archived, who.Id), HttpViewer.Of(who), context.RequestAborted);
        return RoomAsk.Result(result, Results.NoContent);
    }

    /// <summary>
    /// §d: a soft delete. The board has no deletion input yet (U22 follow-up), so a board nobody is subscribed to is
    /// retired at once; live subscribers keep it until a deletion input lets the actor kick them (see U25 decisions).
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        Guid id,
        DeleteBoardRequest body,
        RetroListsRepo repo,
        ActorRegistry registry) {
        var cancellationToken = context.RequestAborted;
        var who = (await context.GetCallerAsync())!.Principal;
        var boardId = new BoardId(id);
        if (await ManagedAsync(repo, boardId, who, Capability.RetroDeleteBoard, cancellationToken) is not { } board) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (!Policy.Can(who, Capability.RetroDeleteBoard, board.Facts, board.Access)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
        }
        if (!string.Equals(body.ConfirmTitle, board.Title, StringComparison.Ordinal)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["confirmTitle"] = "mismatch" });
        }
        if (!await repo.SoftDeleteAsync(boardId, cancellationToken)) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        await registry.TryPurgeAsync(RoomKey.Board(boardId), _ => Task.CompletedTask, cancellationToken);
        if (board.Facts.Team is { } team) {
            context.RequestServices.GetService<ITeamNotices>()?.BoardsChanged(team, boardId);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ListTeamAsync(
        HttpContext context,
        Guid id,
        bool? archived,
        string? cursor,
        int? limit,
        RetroListsRepo repo,
        NpgsqlDataSource dataSource) {
        if (!ListCursor.TryParse(cursor, out var after)) {
            return Invalid("cursor");
        }
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        var page = await repo.ListBoardsAsync(new TeamId(id), archived ?? false, after, ListCursor.Limit(limit), context.RequestAborted);
        return Results.Json(ListPage.Of(page), RoomHttpJsonContext.Default.ListPageBoardListItem);
    }

    private static async Task<IResult> CreateTeamAsync(
        HttpContext context,
        Guid id,
        TeamBoardRequest body,
        RetroListsRepo repo,
        NpgsqlDataSource dataSource,
        HashGate gate) {
        var cancellationToken = context.RequestAborted;
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        if (!TryTitle(body.Title, null, out var title)) {
            return Invalid("title");
        }
        var visibility = body.Visibility ?? BoardVisibility.Team;
        if ((visibility == BoardVisibility.Password) != (body.Password is not null)
            || (body.Password is { } given && !ValidPassword(given))) {
            return Invalid("password");
        }
        var team = new TeamId(id);
        if (await repo.TemplateAsync(team, body.TemplateId, cancellationToken) is not { } template) {
            return Invalid("templateId");
        }
        string? hash = null;
        if (body.Password is { } password) {
            var gated = await gate.RunAsync(() => Bcrypt.Hash(password), cancellationToken);
            if (!gated.Admitted) {
                return HashGate.BusyResult();
            }
            hash = gated.Value;
        }
        var caller = (await context.GetCallerAsync())!;
        var notices = context.RequestServices.GetService<ITeamNotices>();
        return await CreateAsync(repo, team, title, caller.Principal.Id, visibility, hash, template, notices, cancellationToken);
    }

    private static async Task<IResult> ListActionItemsAsync(
        HttpContext context,
        Guid id,
        string? status,
        string? assignee,
        string? sort,
        string? cursor,
        int? limit,
        RetroListsRepo repo,
        NpgsqlDataSource dataSource) {
        if (status is not (null or "open" or "done" or "all")) {
            return Invalid("status");
        }
        bool? done = status switch {
            "done" => true,
            "all" => null,
            _ => false,
        };
        PrincipalId? assigned = null;
        if (assignee is not (null or "unassigned" or "all")) {
            if (!Guid.TryParse(assignee, out var pid)) {
                return Invalid("assignee");
            }
            assigned = new PrincipalId(pid);
        }
        if (sort is not (null or "newest" or "oldest")) {
            return Invalid("sort");
        }
        if (!ListCursor.TryParse(cursor, out var after)) {
            return Invalid("cursor");
        }
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        var filter = new ActionItemFilter(done, assigned, assignee == "unassigned", sort != "oldest");
        var page = await repo.ListActionItemsAsync(new TeamId(id), filter, after, ListCursor.Limit(limit), context.RequestAborted);
        return Results.Json(ListPage.Of(page), RoomHttpJsonContext.Default.ListPageActionItemListItem);
    }

    /// <summary>
    /// The comments of the item's source card, which lives on another board (R22). They are read from that board's
    /// actor and projected by its own rules, so anonymity, hidden author names and <c>mine</c> match the board page.
    /// </summary>
    private static async Task<IResult> ListCommentsAsync(
        HttpContext context,
        Guid id,
        Guid itemId,
        ActionItemCommentsRepo comments,
        ActorRegistry registry,
        NpgsqlDataSource dataSource,
        IAnonTagger tagger) {
        var cancellationToken = context.RequestAborted;
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        if (await comments.SourceAsync(new TeamId(id), new ActionItemId(itemId), cancellationToken) is not { } source) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var who = (await context.GetCallerAsync())!.Principal;
        IReadOnlyList<ActionItemComment>? items;
        try {
            items = await registry.QueryAsync<RetroState, RetroInput, IReadOnlyList<ActionItemComment>?>(
                RoomKey.Board(source.Board),
                view => ValueTask.FromResult(view.State is { } state ? Project(state, source.Card, who.Id, tagger) : null),
                cancellationToken);
        } catch (RoomUnavailableException) {
            return RoomAsk.Unavailable();
        }
        return items is null
            ? Problem.Result(StatusCodes.Status404NotFound, "not_found")
            : Results.Json(new ActionItemComments(items), RoomHttpJsonContext.Default.ActionItemComments);
    }

    private static async Task<IResult> PostCommentAsync(
        HttpContext context,
        Guid id,
        Guid itemId,
        ActionItemCommentRequest body,
        ActorRegistry registry,
        NpgsqlDataSource dataSource) {
        if (body.CommentId is not { } commentId) {
            return Invalid("commentId");
        }
        if (string.IsNullOrWhiteSpace(body.Body)) {
            return Invalid("body");
        }
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        var who = (await context.GetCallerAsync())!.Principal;
        var result = await RetroRoom.AddActionItemCommentAsync(
            registry,
            dataSource,
            new TeamId(id),
            new ActionItemId(itemId),
            commentId,
            body.Body,
            HttpViewer.Of(who),
            context.RequestAborted);
        return RoomAsk.Result(result, () => Results.Json(
            new ActionItemCommentPosted(commentId),
            RoomHttpJsonContext.Default.ActionItemCommentPosted,
            statusCode: StatusCodes.Status201Created));
    }

    private static IReadOnlyList<ActionItemComment> Project(RetroState state, CardId card, PrincipalId viewer, IAnonTagger tagger) {
        var snapshot = RetroProjection.Snapshot(state, [], forMembers: false);
        var mine = new RetroProjection(tagger).Overlay(state, viewer).MineComments.ToHashSet();
        return [
            .. snapshot.Comments
                .Where(x => x.CardId == card)
                .Select(x => new ActionItemComment(x.Id, x.Body, x.Anonymous, x.CreatedAt, x.Author, mine.Contains(x.Id))),
        ];
    }

    private static async Task<ManagedBoard?> ManagedAsync(
        RetroListsRepo repo,
        BoardId board,
        Principal who,
        Capability capability,
        CancellationToken cancellationToken) {
        var managed = await repo.BoardAsync(board, who.Id, cancellationToken);
        // A board the caller cannot see is missing to them, whatever they may manage.
        if (managed is null
            || managed.Facts.Deleted
            || managed.Facts.TeamDeleted
            || (!Policy.Can(who, Capability.Subscribe, managed.Facts, managed.Access)
                && !Policy.Can(who, capability, managed.Facts, managed.Access))) {
            return null;
        }
        return managed;
    }

    private static async Task<IResult> CreateAsync(
        RetroListsRepo repo,
        TeamId? team,
        string title,
        PrincipalId createdBy,
        BoardVisibility visibility,
        string? passwordHash,
        BoardTemplate template,
        ITeamNotices? notices,
        CancellationToken cancellationToken) {
        var board = new BoardId(Guid.CreateVersion7());
        var code = await RoomCodes.CreateAsync(x => repo.CreateBoardAsync(
            new NewBoard(board, x, team, title, createdBy, visibility, passwordHash, template),
            cancellationToken));
        if (code is not { } created) {
            return RoomAsk.Unavailable();
        }
        if (team is { } owner) {
            notices?.BoardsChanged(owner, board);
        }
        return Results.Json(
            new RoomCreated(board.Value, created.Value),
            RoomHttpJsonContext.Default.RoomCreated,
            statusCode: StatusCodes.Status201Created);
    }

    private static bool TryTitle(string? given, string? fallback, out string title) {
        title = given?.Trim() is { Length: > 0 } trimmed ? trimmed : fallback ?? "";
        return title.Length is > 0 and <= MaxTitleLength && !title.Any(char.IsControl);
    }

    // bcrypt reads 72 bytes at most; a longer password would be accepted with any suffix.
    private static bool ValidPassword(string password) {
        var bytes = Encoding.UTF8.GetByteCount(password);
        return bytes is >= MinPasswordBytes and <= MaxPasswordBytes;
    }

    private static IResult Invalid(string field) =>
        Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { [field] = "invalid" });
}
