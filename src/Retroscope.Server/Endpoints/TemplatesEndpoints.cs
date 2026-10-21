using System.Text.Json.Serialization;
using Retroscope.Core.Authz;
using Retroscope.Core.Protocol;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

public sealed record TemplatesPage(
    IReadOnlyList<TemplateInfo> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateColumnRequest(string? Title, ColumnColor? Color, bool? ActionItems);

/// <param name="MaxVotes">Null means unlimited.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateConfigRequest(
    bool? AllowAnonymous = null,
    bool? VotingEnabled = null,
    int? MaxVotes = null,
    bool? ShowAuthorNames = null,
    bool? StagesEnabled = null,
    bool? EnforceReadiness = null,
    bool? AllowSelfVotes = null,
    string? VoteEmoji = null);

/// <summary>A whole template: POST creates one, PUT replaces one (the columns always as the full list).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateRequest(
    string? Name,
    List<TemplateColumnRequest>? Columns,
    bool? IsDefault = null,
    TemplateConfigRequest? Config = null);

public static class TemplatesEndpoints {
    public const int MaxNameLength = 100;
    public const int MaxColumns = 20;
    public const int MaxColumnTitleLength = 100;
    public const int MaxVotes = 99;
    public const int MaxVoteEmojiLength = 8;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<TemplatesRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/teams/{id:guid}/templates", ListAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(TeamsEndpoints.Read);
        app.MapPost("/api/teams/{id:guid}/templates", CreateAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
        app.MapPut("/api/teams/{id:guid}/templates/{templateId:guid}", ReplaceAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
        app.MapDelete("/api/teams/{id:guid}/templates/{templateId:guid}", DeleteAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
    }

    private static async Task<IResult> ListAsync(Guid id, HttpContext context, TeamAccess access, TemplatesRepo templates) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var list = await templates.ListAsync(check.TeamId, context.RequestAborted);
        return Results.Json(new TemplatesPage(list, null), TeamHttpJsonContext.Default.TemplatesPage);
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        TemplateRequest body,
        HttpContext context,
        TeamAccess access,
        TemplatesRepo templates,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var now = time.GetUtcNow();
        var fields = new Dictionary<string, string>();
        if (Validate(Guid.CreateVersion7(now), body, fields) is not { } template) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        await templates.CreateAsync(check.TeamId, template, now, context.RequestAborted);
        return Results.Json(template, TeamHttpJsonContext.Default.TemplateInfo, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id,
        Guid templateId,
        TemplateRequest body,
        HttpContext context,
        TeamAccess access,
        TemplatesRepo templates,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var fields = new Dictionary<string, string>();
        if (Validate(templateId, body, fields) is not { } template) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        return await templates.ReplaceAsync(check.TeamId, template, time.GetUtcNow(), context.RequestAborted)
            ? Results.Json(template, TeamHttpJsonContext.Default.TemplateInfo)
            : TeamAccess.NotFound();
    }

    private static async Task<IResult> DeleteAsync(Guid id, Guid templateId, HttpContext context, TeamAccess access, TemplatesRepo templates) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        return await templates.DeleteAsync(check.TeamId, templateId, context.RequestAborted)
            ? Results.NoContent()
            : TeamAccess.NotFound();
    }

    /// <summary>The template the request describes; null, with every broken rule in <paramref name="fields"/>, when invalid.</summary>
    private static TemplateInfo? Validate(Guid id, TemplateRequest body, Dictionary<string, string> fields) {
        var name = TeamText.Line(body.Name, MaxNameLength, fields, "name", required: true);
        var columns = new List<TemplateColumn>();
        if (body.Columns is not { Count: > 0 and <= MaxColumns }) {
            fields["columns"] = "invalid";
        } else {
            foreach (var column in body.Columns) {
                var title = TeamText.Line(column?.Title, MaxColumnTitleLength, fields, "columns", required: true);
                if (column?.Color is not { } color) {
                    fields["columns"] = "invalid";
                    continue;
                }
                if (title is not null) {
                    columns.Add(new TemplateColumn(title, color, column.ActionItems ?? false));
                }
            }
            if (columns.Count(x => x.ActionItems) > 1) {
                fields["columns"] = "invalid";
            }
        }
        var config = Config(body.Config, fields);
        if (fields.Count > 0) {
            return null;
        }
        return new TemplateInfo(id, name!, body.IsDefault ?? false, columns, config);
    }

    private static TemplateConfig Config(TemplateConfigRequest? request, Dictionary<string, string> fields) {
        var defaults = TemplateConfig.Default;
        if (request is null) {
            return defaults;
        }
        if (request.MaxVotes is < 1 or > MaxVotes) {
            fields["config.maxVotes"] = "invalid";
        }
        var emoji = request.VoteEmoji is null
            ? defaults.VoteEmoji
            : TeamText.Line(request.VoteEmoji, MaxVoteEmojiLength, fields, "config.voteEmoji", required: true);
        return new TemplateConfig(
            request.AllowAnonymous ?? defaults.AllowAnonymous,
            request.VotingEnabled ?? defaults.VotingEnabled,
            request.MaxVotes,
            request.ShowAuthorNames ?? defaults.ShowAuthorNames,
            request.StagesEnabled ?? defaults.StagesEnabled,
            request.EnforceReadiness ?? defaults.EnforceReadiness,
            request.AllowSelfVotes ?? defaults.AllowSelfVotes,
            emoji ?? defaults.VoteEmoji);
    }
}
