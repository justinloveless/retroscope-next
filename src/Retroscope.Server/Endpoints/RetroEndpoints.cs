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

/// <param name="TemplateId">One of the team's templates; the team's default template, else the built-in columns.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeamBoardRequest(string? Title, Guid? TemplateId);

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
/// Team boards over HTTP (§d): look up by code, archive, delete, the team's board list and board creation, and the
/// team page's action items with their comments. Every board belongs to a team and opens to its members only (UX18).
/// </summary>
public static class RetroEndpoints {
    public const int MaxTitleLength = 200;

    public static readonly RateLimit Manage = RateLimit.Define(
        "retro.manage", RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)), RateRule.PerIp(60, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit CommentPost = RateLimit.Define(
        "team.action.comment", RateRule.PerPrincipal(60, TimeSpan.FromMinutes(1)), RateRule.PerIp(120, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<RetroListsRepo>();
        services.AddSingleton<ActionItemCommentsRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        // RoomResolver limits every code lookup itself (30/min per IP, S9), whichever route makes it. Only a member can
        // be admitted, so a caller who is not signed in is refused before any lookup is spent (UX18).
        app.MapGet("/api/retro/by-code/{code}", ByCodeAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(RateLimit.Unlimited);
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
        app.MapGet("/api/teams/{id:guid}/action-items/{itemId:guid}/comments", ListCommentsAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(PokerEndpoints.TeamRead);
        app.MapPost("/api/teams/{id:guid}/action-items/{itemId:guid}/comments", PostCommentAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(CommentPost);
    }

    private static async Task<IResult> ByCodeAsync(HttpContext context, string code, RoomResolver resolver) {
        var resolution = await resolver.ResolveAsync(context, RoomKind.Board, code, context.RequestAborted);
        if (resolution is not RoomResolution.Found found) {
            return RoomResolver.Result(resolution);
        }
        return Results.Json(new RoomLookup(found.Room.Id, RoomLookup.Granted), RoomHttpJsonContext.Default.RoomLookup);
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
    /// §d: a soft delete, then Ask → the board kicks every subscriber (<c>deleted</c>) and the actor is retired, so the
    /// next subscribe loads the board as missing.
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
        var room = RoomKey.Board(boardId);
        // The row is gone either way: a board that could not be told loads as missing when it next starts.
        await registry.AskAsync<RetroState, RetroInput>(room, new RetroInput.Delete(), HttpViewer.Of(who), cancellationToken);
        await registry.TryPurgeAsync(room, _ => Task.CompletedTask, cancellationToken);
        context.RequestServices.GetService<ITeamNotices>()?.BoardsChanged(board.Facts.Team, boardId);
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
        NpgsqlDataSource dataSource) {
        var cancellationToken = context.RequestAborted;
        if (await TeamGate.RoleAsync(context, dataSource, id) is null) {
            return TeamGate.NotMember();
        }
        if (!TryTitle(body.Title, out var title)) {
            return Invalid("title");
        }
        var team = new TeamId(id);
        if (await repo.TemplateAsync(team, body.TemplateId, cancellationToken) is not { } template) {
            return Invalid("templateId");
        }
        var caller = (await context.GetCallerAsync())!;
        var notices = context.RequestServices.GetService<ITeamNotices>();
        return await CreateAsync(repo, team, title, caller.Principal.Id, template, notices, cancellationToken);
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
        var snapshot = RetroProjection.Snapshot(state, []);
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
        TeamId team,
        string title,
        PrincipalId createdBy,
        BoardTemplate template,
        ITeamNotices? notices,
        CancellationToken cancellationToken) {
        var board = new BoardId(Guid.CreateVersion7());
        var code = await RoomCodes.CreateAsync(x => repo.CreateBoardAsync(new NewBoard(board, x, team, title, createdBy, template), cancellationToken));
        if (code is not { } created) {
            return RoomAsk.Unavailable();
        }
        notices?.BoardsChanged(team, board);
        return Results.Json(
            new RoomCreated(board.Value, created.Value),
            RoomHttpJsonContext.Default.RoomCreated,
            statusCode: StatusCodes.Status201Created);
    }

    private static bool TryTitle(string? given, out string title) {
        title = given?.Trim() ?? "";
        return title.Length is > 0 and <= MaxTitleLength && !title.Any(char.IsControl);
    }

    private static IResult Invalid(string field) =>
        Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { [field] = "invalid" });
}
