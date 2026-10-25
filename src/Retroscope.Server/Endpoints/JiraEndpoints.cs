using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using Retroscope.Core.Adf;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Core.Poker;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;

namespace Retroscope.Server.Endpoints;

// Requests. Members a request does not name are refused (§d).

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraConfigRequest(
    string? SiteHost,
    string? Email,
    string? Token,
    string? ProjectKey,
    long? BoardId,
    string? TicketPrefix,
    string? PointsFieldId,
    bool? MembersCanEdit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraTestRequest;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraMetaRequest(IReadOnlyList<string>? Keys);

/// <param name="Description">Markup that may hold <c>[[island:id]]</c> refs; needs <paramref name="DescriptionBase"/>.</param>
/// <param name="DescriptionBase">The issue's <c>updated</c> the edit started from.</param>
/// <param name="AssigneeAccountId">Absent keeps the assignee, null unassigns.</param>
/// <param name="DescriptionAdf">The rich editor's whole description (UX10), tokens included; null clears it. Exclusive
/// with <paramref name="Description"/>; needs <paramref name="DescriptionBase"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraIssuePatch(
    string? Summary,
    string? Description,
    string? DescriptionBase,
    JsonElement AssigneeAccountId,
    string? PriorityId,
    string? IssueTypeId,
    JsonElement DescriptionAdf = default);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraTransitionRequest(string? TransitionId);

/// <param name="Points">A number, or null to clear the field.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraPointsRequest(JsonElement Points);

/// <param name="SprintId">A sprint id, or null for the board's backlog.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraSprintRequest(JsonElement SprintId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraCommentRequest(string? Body);

/// <param name="Direction"><c>outward</c> (default): this issue is the link's outward side, e.g. it "blocks" the target.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraLinkCreateRequest(string? TypeId, string? TargetKey, string? Direction);

/// <param name="Description">Markup in the Jira dialect, or in the retro dialect when <paramref name="FromRetro"/>.</param>
/// <param name="CloneFrom">Copies the type, project and description (islands verbatim) of this issue.</param>
/// <param name="FromRetro">The description is a retro action item's text ("Create in Jira"): mentions become
/// <c>@Name</c> and board images their alt text.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraCreateRequest(
    string? Summary,
    string? Description,
    string? CloneFrom,
    string? ProjectKey,
    string? IssueTypeId,
    bool? FromRetro);

// Responses (the shapes docs/decisions/U30.md and the web fake server use). Timestamps of our own rows are Unix
// milliseconds; Jira's dates are passed through as Jira wrote them.

public sealed record JiraConfigView(
    bool Configured,
    string? SiteHost,
    string? ProjectKey,
    long? BoardId,
    string? TicketPrefix,
    string? PointsFieldId,
    string? SprintFieldId,
    bool MembersCanEdit,
    string Status,
    long? LastCheckedAt,
    string? EmailMasked,
    string? TokenLast4);

public sealed record JiraDiscovered(string? PointsFieldId, string? SprintFieldId, long? BoardId);

public sealed record JiraTestView(bool Ok, string? AccountName, JiraDiscovered Discovered);

public sealed record JiraRowView(
    string Key,
    string? Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string? Assignee,
    decimal? Points,
    string? ParentKey,
    string? ParentSummary,
    string? RoundState);

public sealed record JiraBucketView(string Id, string Kind, string Name, string? State, string? StartDate, IReadOnlyList<JiraRowView> Issues);

public sealed record JiraBrowseView(IReadOnlyList<JiraBucketView> Buckets, bool Partial, string? Code);

public sealed record JiraMetaItem(
    string Key,
    string? Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    decimal? Points,
    string? ParentKey,
    string? ParentSummary);

public sealed record JiraMetaView(IReadOnlyList<JiraMetaItem> Items);

public sealed record JiraUserView(string? AccountId, string? DisplayName);

public sealed record JiraParentView(string Key, string? Summary);

public sealed record JiraSprintView(long? Id, string Name, string? State, string? StartDate);

public sealed record JiraIssueFieldsView(
    string? Summary,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string? PriorityId,
    string? IssueType,
    string? IssueTypeId,
    JiraUserView? Assignee,
    JiraUserView? Reporter,
    JiraParentView? Parent,
    IReadOnlyList<string> Labels,
    decimal? Points,
    JiraSprintView? Sprint,
    string? Created,
    string? Project);

public sealed record JiraCommentView(string Id, string? Author, string? Created, Doc Body);

public sealed record JiraCommentPageView(int StartAt, int Total, IReadOnlyList<JiraCommentView> Comments);

public sealed record JiraAttachmentView(string Id, string? Filename, string? MimeType, long Size);

public sealed record JiraLinkView(string? Id, string? Type, string? Label, string Direction, string? Key, string? Summary, string? Status);

/// <param name="Editable">False when the description is not an ADF document; the drawer then offers no description edit.</param>
/// <param name="DescriptionMarkup">The editable description as markup with island refs; null when not editable.</param>
public sealed record JiraIssueView(
    string Key,
    string Updated,
    JiraIssueFieldsView Fields,
    Doc Description,
    bool Editable,
    string? DescriptionMarkup,
    IReadOnlyList<string> IslandIds,
    IReadOnlyList<JiraCommentView> Comments,
    int CommentTotal,
    IReadOnlyList<JiraAttachmentView> Attachments,
    IReadOnlyList<JiraLinkView> Links);

public sealed record JiraNamedView(string Id, string? Name);

public sealed record JiraLinkTypeView(string Id, string? Name, string? Inward, string? Outward);

public sealed record JiraTransitionView(string Id, string? Name, string? To, string? StatusCategory);

public sealed record JiraOptionsView(
    IReadOnlyList<JiraNamedView> IssueTypes,
    IReadOnlyList<JiraNamedView> Priorities,
    IReadOnlyList<JiraLinkTypeView> LinkTypes,
    IReadOnlyList<JiraTransitionView> Transitions);

public sealed record JiraSprintItem(long Id, string? Name, string? State, string? StartDate, string? EndDate);

public sealed record JiraSprintsView(IReadOnlyList<JiraSprintItem> Sprints);

public sealed record JiraAssignableView(IReadOnlyList<JiraUserView> Users);

public sealed record JiraPointsView(string Key, decimal? Points);

public sealed record JiraCreatedView(string Key);

public sealed record JiraStaleCurrent(string Updated);

/// <summary>problem+json for the proxy: §f's codes, Jira's field messages, and the wait Jira asked for.</summary>
public sealed record JiraProblemBody(
    string Type,
    string Title,
    int Status,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JiraStaleCurrent? Current = null);

/// <summary>Who is calling about which team, after the team, membership and capability checks.</summary>
public sealed record JiraCall(TeamId Team, Principal Who, JiraTeamConfig? Config, bool IsAdmin) {
    public JiraTeamConfig RequiredConfig => Config ?? throw new JiraException(JiraError.NotConfigured);
}

/// <summary>
/// The checks in front of every Jira route: the team exists and is not deleted, the caller is a member (a non-member
/// gets the same 404 as a missing team), Jira is configured where the route needs it, and the capability holds —
/// writes need <see cref="Capability.JiraEditIssues"/> (members when <c>members_can_edit</c>, else admins), settings
/// need <see cref="Capability.JiraConfigure"/>.
/// </summary>
public sealed class JiraGate(NpgsqlDataSource dataSource, IMembershipLookup memberships, IAccessView access, Credentials credentials) {
    public async Task<(JiraCall? Call, IResult? Refusal)> CheckAsync(
        HttpContext context,
        Guid teamId,
        Capability capability,
        bool needsConfig) {
        var caller = await context.GetCallerAsync();
        var team = new TeamId(teamId);
        if (caller is null || !await TeamLiveAsync(team, context.RequestAborted)) {
            return (null, Problem.Result(StatusCodes.Status404NotFound, "not_found"));
        }
        await memberships.LoadAsync(team, context.RequestAborted);
        var config = await credentials.LoadAsync(team, context.RequestAborted);
        var facts = new ResourceFacts(
            RoomKey.Team(team),
            Team: team,
            JiraConfigured: config is not null,
            MembersCanEditJira: config?.MembersCanEdit ?? true);
        var who = caller.Principal;
        if (!Policy.Can(who, Capability.Subscribe, facts, access)) {
            return (null, Problem.Result(StatusCodes.Status404NotFound, "not_found"));
        }
        if (needsConfig && config is null) {
            return (null, Problem.Result(StatusCodes.Status409Conflict, JiraErrorCode.NotConfigured));
        }
        if (!Policy.Can(who, capability, facts, access)) {
            return (null, Problem.Result(StatusCodes.Status403Forbidden, "forbidden"));
        }
        var admin = Policy.Can(who, Capability.JiraConfigure, facts, access);
        return (new JiraCall(team, who, config, admin), null);
    }

    private async Task<bool> TeamLiveAsync(TeamId team, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("select deleted_at is null from teams where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}

/// <summary>
/// The Jira proxy (<c>/api/teams/{id}/jira/...</c>, DESIGN §f) and the team's Jira settings (§d). Jira bodies are
/// never forwarded: every answer is built from the typed client's documents. Every write invalidates the cached
/// entries that show the issue, refreshes the issue in the team's live poker rooms and leaves an audit row.
/// </summary>
public static partial class JiraEndpoints {
    public const string ScopedTokenHint = "Jira refused the token. Use a classic (unscoped) Atlassian API token.";
    public const int MaxSummaryLength = 255;
    public const int MaxSearchLength = 200;
    public const decimal MaxPoints = 1000;

    public static readonly TimeSpan OptionsTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan TransitionsTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AssignableTtl = TimeSpan.FromSeconds(60);

    public static readonly RateLimit Read = RateLimit.Define(
        "jira.read",
        RateRule.PerPrincipal(600, TimeSpan.FromMinutes(1)),
        RateRule.PerIp(1200, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Write = RateLimit.Define(
        "jira.write",
        RateRule.PerPrincipal(120, TimeSpan.FromMinutes(1)),
        RateRule.PerIp(240, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Settings = RateLimit.Define(
        "jira.settings",
        RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)),
        RateRule.PerIp(60, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Attachment = RateLimit.Define(
        "jira.attachment",
        RateRule.PerPrincipal(300, TimeSpan.FromMinutes(1)),
        RateRule.PerIp(600, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<BrowseService>();
        services.AddSingleton<IslandStore>();
        services.AddSingleton<SprintReportService>();
        services.AddSingleton<JiraAudit>();
        services.AddSingleton<JiraGate>();
        services.AddSingleton<JiraWrites>();
        services.AddSingleton(sp => new PokerJiraPort(
            sp.GetRequiredService<JiraClient>,
            sp.GetRequiredService<BrowseService>,
            sp.GetRequiredService<JiraCache>(),
            sp.GetRequiredService<JiraAudit>(),
            sp.GetRequiredService<ILogger<PokerJiraPort>>()));
        services.AddSingleton<IPokerJiraPort>(sp => sp.GetRequiredService<PokerJiraPort>());
    }

    public static void Map(IEndpointRouteBuilder app) {
        var jira = app.MapGroup("/api/teams/{teamId:guid}/jira");
        jira.MapGet("", GetConfigAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPut("", PutConfigAsync).RequireRoutePolicy(RoutePolicy.TeamAdmin).RequireRateLimit(Settings);
        jira.MapDelete("", DeleteConfigAsync).RequireRoutePolicy(RoutePolicy.TeamAdmin).RequireRateLimit(Settings);
        jira.MapPost("/test", TestAsync).RequireRoutePolicy(RoutePolicy.TeamAdmin).RequireRateLimit(Settings);

        jira.MapGet("/browse", BrowseAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPost("/meta", MetaAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapGet("/sprints", SprintsAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapGet("/sprint-report", SprintReportAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPost("/issues", CreateAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapGet("/issues/{key}", GetIssueAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapGet("/issues/{key}/description", DescriptionAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPatch("/issues/{key}", PatchIssueAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Write)
            .WithMetadata(new RequestBodyLimit(MaxPatchBytes));
        jira.MapGet("/issues/{key}/comments", CommentsAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPost("/issues/{key}/comments", AddCommentAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapPost("/issues/{key}/transition", TransitionAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapPut("/issues/{key}/points", PointsAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapPut("/issues/{key}/sprint", SprintAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapGet("/issues/{key}/options", OptionsAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapGet("/issues/{key}/assignable", AssignableAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Read);
        jira.MapPost("/issues/{key}/links", CreateLinkAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapDelete("/issues/{key}/links/{linkId}", DeleteLinkAsync).RequireRoutePolicy(RoutePolicy.TeamMember).RequireRateLimit(Write);
        jira.MapGet("/issues/{key}/attachments/{attachmentId}", AttachmentAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Attachment);
    }

    // Settings.

    private static async Task<IResult> GetConfigAsync(HttpContext context, Guid teamId, JiraGate gate) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.Subscribe, needsConfig: false);
        return refusal ?? Ok(ConfigView(call!.Config, call.IsAdmin), JiraHttpJsonContext.Default.JiraConfigView);
    }

    private static async Task<IResult> PutConfigAsync(
        HttpContext context,
        Guid teamId,
        JiraConfigRequest body,
        JiraGate gate,
        Credentials credentials,
        JiraClient client,
        JiraHostPolicy hostPolicy,
        JiraCache cache,
        JiraAudit audit,
        NpgsqlDataSource dataSource,
        ActorRegistry registry) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.JiraConfigure, needsConfig: false);
        if (refusal is not null) {
            return refusal;
        }
        var team = call!.Team;
        credentials.Invalidate(team);
        var existing = await credentials.LoadAsync(team, context.RequestAborted);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!hostPolicy.TryNormalize(body.SiteHost, out var host)) {
            fields["siteHost"] = string.IsNullOrWhiteSpace(body.SiteHost) ? "required" : "not_allowed";
        }
        var token = Blank(body.Token) ? null : body.Token!.Trim();
        var email = Blank(body.Email) ? null : body.Email!.Trim();
        // A hijacked admin session must not be able to point the stored token at another host.
        if (token is null && (existing is null || (host is not null && host != existing.SiteHost))) {
            fields["token"] = "required";
        }
        if (email is not null && (email.Length > 254 || !email.Contains('@', StringComparison.Ordinal))) {
            fields["email"] = "invalid";
        }
        if (token is not null && (token.Length is < 4 or > 1024 || token.Any(char.IsControl))) {
            fields["token"] = "invalid";
        }
        var projectKey = Blank(body.ProjectKey) ? null : body.ProjectKey!.Trim();
        var ticketPrefix = Blank(body.TicketPrefix) ? null : body.TicketPrefix!.Trim();
        var pointsFieldId = Blank(body.PointsFieldId) ? null : body.PointsFieldId!.Trim();
        if (projectKey is not null && !ProjectKey().IsMatch(projectKey)) {
            fields["projectKey"] = "invalid";
        }
        if (ticketPrefix is not null && !ProjectKey().IsMatch(ticketPrefix)) {
            fields["ticketPrefix"] = "invalid";
        }
        if (body.BoardId is <= 0) {
            fields["boardId"] = "invalid";
        }
        if (pointsFieldId is not null && !JiraMap.IsCustomFieldId(pointsFieldId)) {
            fields["pointsFieldId"] = "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status422UnprocessableEntity, "validation", fields);
        }

        var stored = existing is null ? null : OpenStored(credentials, existing);
        email ??= stored?.Email;
        if (email is null || (token is null && stored is null)) {
            return Problem.Result(
                StatusCodes.Status422UnprocessableEntity,
                "validation",
                new Dictionary<string, string> { [email is null ? "email" : "token"] = "required" });
        }
        JiraProbeResult probe;
        try {
            probe = await client.ProbeAsync(team, host!, email, token ?? stored!.Value.Token, projectKey, body.BoardId, context.RequestAborted);
        } catch (JiraException e) {
            return ProbeProblem(e.Error);
        }

        var sealedBox = token is not null || email != stored?.Email
            ? credentials.Seal(team, email, token ?? stored!.Value.Token)
            : new SealedJiraCredentials(existing!.CredentialsBox, existing.KeyId, existing.TokenLast4, existing.EmailMasked);
        // Only a newly entered token ends the imported-token banner (S10).
        var status = token is null && existing?.Status == JiraConfigStatus.RotateRequired
            ? JiraConfigStatus.RotateRequired
            : JiraConfigStatus.Ok;
        var membersCanEdit = body.MembersCanEdit ?? existing?.MembersCanEdit ?? true;
        await using (var command = dataSource.CreateCommand("""
            insert into team_jira_config (team_id, site_host, credentials_box, key_id, token_last4, email_masked, project_key,
                board_id, ticket_prefix, points_field_id, points_field_overridden, sprint_field_id, status, members_can_edit,
                last_checked_at, updated_by, updated_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, now(), $15, now())
            on conflict (team_id) do update set
                site_host = excluded.site_host, credentials_box = excluded.credentials_box, key_id = excluded.key_id,
                token_last4 = excluded.token_last4, email_masked = excluded.email_masked, project_key = excluded.project_key,
                board_id = excluded.board_id, ticket_prefix = excluded.ticket_prefix, points_field_id = excluded.points_field_id,
                points_field_overridden = excluded.points_field_overridden, sprint_field_id = excluded.sprint_field_id,
                status = excluded.status, members_can_edit = excluded.members_can_edit,
                last_checked_at = excluded.last_checked_at, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """)) {
            command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = host! });
            command.Parameters.Add(new NpgsqlParameter { Value = sealedBox.Box });
            command.Parameters.Add(new NpgsqlParameter { Value = sealedBox.KeyId });
            command.Parameters.Add(new NpgsqlParameter { Value = sealedBox.TokenLast4 });
            command.Parameters.Add(new NpgsqlParameter { Value = sealedBox.EmailMasked });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)(projectKey ?? probe.Board?.Location?.ProjectKey) ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)(probe.Board?.Id ?? body.BoardId) ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)ticketPrefix ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)(pointsFieldId ?? probe.Fields.PointsFieldId) ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = pointsFieldId is not null });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)probe.Fields.SprintFieldId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = Credentials.StatusText(status) });
            command.Parameters.Add(new NpgsqlParameter { Value = membersCanEdit });
            command.Parameters.Add(new NpgsqlParameter { Value = call.Who.Id.Value });
            await command.ExecuteNonQueryAsync(context.RequestAborted);
        }

        var config = await SettingsChangedAsync(team, credentials, cache, dataSource, registry, context.RequestAborted);
        await audit.WriteAsync(team, call.Who.Id, JiraAudit.ConfigSet, host, CancellationToken.None);
        return Ok(ConfigView(config, isAdmin: true), JiraHttpJsonContext.Default.JiraConfigView);
    }

    private static async Task<IResult> DeleteConfigAsync(
        HttpContext context,
        Guid teamId,
        JiraGate gate,
        Credentials credentials,
        JiraCache cache,
        JiraAudit audit,
        NpgsqlDataSource dataSource,
        ActorRegistry registry) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.JiraConfigure, needsConfig: false);
        if (refusal is not null) {
            return refusal;
        }
        await using (var command = dataSource.CreateCommand("delete from team_jira_config where team_id = $1")) {
            command.Parameters.Add(new NpgsqlParameter { Value = call!.Team.Value });
            await command.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await SettingsChangedAsync(call.Team, credentials, cache, dataSource, registry, context.RequestAborted);
        await audit.WriteAsync(call.Team, call.Who.Id, JiraAudit.ConfigDelete, null, CancellationToken.None);
        return Results.NoContent();
    }

    private static async Task<IResult> TestAsync(
        HttpContext context,
        Guid teamId,
        JiraTestRequest body,
        JiraGate gate,
        Credentials credentials,
        JiraClient client) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.JiraConfigure, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        var config = call!.RequiredConfig;
        try {
            var myself = await client.GetMyselfAsync(call.Team, context.RequestAborted);
            var discovered = JiraMap.DiscoverFields(await client.GetFieldsAsync(call.Team, context.RequestAborted));
            JiraBoard? board = config.BoardId is { } id ? await client.GetBoardAsync(call.Team, id, context.RequestAborted) : null;
            if (config.Status is not JiraConfigStatus.RotateRequired) {
                await credentials.MarkStatusAsync(call.Team, JiraConfigStatus.Ok, context.RequestAborted);
            }
            return Ok(
                new JiraTestView(true, myself.DisplayName, new JiraDiscovered(discovered.PointsFieldId, discovered.SprintFieldId, board?.Id)),
                JiraHttpJsonContext.Default.JiraTestView);
        } catch (JiraException e) {
            return ProbeProblem(e.Error);
        }
    }

    // Browse and metadata.

    private static async Task<IResult> BrowseAsync(
        HttpContext context,
        Guid teamId,
        string? q,
        string? status,
        string? points,
        string? scope,
        Guid? session,
        bool? hidePointedOrPointing,
        JiraGate gate,
        BrowseService browse,
        JiraClient client) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.Subscribe, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsedStatus = ParseStatus(status) ?? Invalid(fields, "status", BrowseStatus.All);
        var parsedPoints = ParsePoints(points) ?? Invalid(fields, "points", BrowsePoints.Any);
        var parsedScope = ParseScope(scope) ?? Invalid(fields, "scope", BrowseScope.Board);
        if (q is { Length: > MaxSearchLength }) {
            fields["q"] = "too_long";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }

        return await GuardAsync(async () => {
            // A stored field is never resolved away, so only a team without one can lack an effective points field.
            if (parsedPoints.Kind != BrowsePointsKind.Any && call!.RequiredConfig.PointsFieldId is null
                && (await client.EffectiveConfigAsync(call.Team, context.RequestAborted)).PointsFieldId is null) {
                return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["points"] = "no_points_field" });
            }
            var query = new BrowseQuery(Blank(q) ? null : q!.Trim(), parsedStatus, parsedPoints, parsedScope);
            var result = await browse.BrowseAsync(call!.Team, query, context.RequestAborted);
            var states = session is { } id
                ? await browse.RoundStatesAsync(call.Team, new SessionId(id), context.RequestAborted)
                : new Dictionary<string, string>();
            var rows = result.Rows.ToList();
            if (!query.Searching && states.Count > 0) {
                // Tickets already on the session's rounds always show, so their Pointing/Pointed reason is visible.
                var shown = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
                rows.AddRange(await browse.RowsAsync(call.Team, states.Keys.Where(key => !shown.Contains(key)), context.RequestAborted));
            }
            // Parity: the hide filter does not apply while searching (a typed key or text always finds its ticket).
            if (hidePointedOrPointing == true && !query.Searching) {
                rows.RemoveAll(row => states.ContainsKey(row.Key));
            }
            var buckets = BrowseService.Buckets(rows)
                .Select(bucket => new JiraBucketView(
                    bucket.Id,
                    bucket.Backlog ? "backlog" : "sprint",
                    bucket.Name,
                    bucket.State,
                    bucket.StartDate,
                    [.. bucket.Rows.Select(row => RowView(row, states.GetValueOrDefault(row.Key)))]))
                .ToList();
            return Ok(new JiraBrowseView(buckets, result.Partial, result.Code), JiraHttpJsonContext.Default.JiraBrowseView);
        });
    }

    private static async Task<IResult> MetaAsync(HttpContext context, Guid teamId, JiraMetaRequest body, JiraGate gate, BrowseService browse) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.Subscribe, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        if (body.Keys is not { } keys || keys.Count > BrowseService.MaxMetaKeys || keys.Any(key => key is null)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["keys"] = "invalid" });
        }
        return await GuardAsync(async () => {
            var rows = await browse.RowsAsync(call!.Team, keys, context.RequestAborted);
            return Ok(
                new JiraMetaView([.. rows.Select(row => new JiraMetaItem(
                    row.Key, row.Summary, row.IssueType, row.Status, row.StatusCategory, row.Points, row.ParentKey, row.ParentSummary))]),
                JiraHttpJsonContext.Default.JiraMetaView);
        });
    }

    private static async Task<IResult> SprintsAsync(HttpContext context, Guid teamId, JiraGate gate, BrowseService browse) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.Subscribe, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        return await GuardAsync(async () => {
            var sprints = await browse.SprintsAsync(call!.Team, context.RequestAborted);
            return Ok(
                new JiraSprintsView([.. sprints.Select(s => new JiraSprintItem(s.Id, s.Name, s.State, s.StartDate, s.EndDate))]),
                JiraHttpJsonContext.Default.JiraSprintsView);
        });
    }

    // Issue reads.

    private static async Task<IResult> GetIssueAsync(HttpContext context, Guid teamId, string key, JiraGate gate, IslandStore islands) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.Subscribe, gate);
        if (refusal is not null) {
            return refusal;
        }
        return await GuardAsync(async () => {
            var detail = await islands.GetAsync(call!.Team, key, context.RequestAborted);
            return Ok(IssueView(detail), JiraHttpJsonContext.Default.JiraIssueView);
        });
    }

    private static async Task<IResult> CommentsAsync(
        HttpContext context,
        Guid teamId,
        string key,
        int? startAt,
        JiraGate gate,
        JiraClient client,
        IslandStore islands) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.Subscribe, gate);
        if (refusal is not null) {
            return refusal;
        }
        if (startAt is < 0 or > 100_000) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["startAt"] = "invalid" });
        }
        return await GuardAsync(async () => {
            var attachments = await islands.AttachmentsAsync(call!.Team, key, context.RequestAborted);
            var page = await client.GetCommentsAsync(call.Team, key, startAt ?? 0, IslandStore.FirstComments, context.RequestAborted);
            var comments = (page.Comments ?? []).Select(comment => CommentView(IslandStore.Render(comment, attachments))).ToList();
            return Ok(new JiraCommentPageView(page.StartAt, page.Total, comments), JiraHttpJsonContext.Default.JiraCommentPageView);
        });
    }

    private static async Task<IResult> OptionsAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraGate gate,
        JiraClient client,
        JiraCache cache,
        IslandStore islands) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.Subscribe, gate);
        if (refusal is not null) {
            return refusal;
        }
        return await GuardAsync(async () => {
            var team = call!.Team;
            var detail = await islands.GetAsync(team, key, context.RequestAborted);
            var editMeta = await cache.GetOrCreateAsync(
                JiraCache.Key(team, "editmeta", key),
                OptionsTtl,
                async () => {
                    var meta = await client.GetEditMetaAsync(team, key, CancellationToken.None);
                    return (meta, 2048L, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team), JiraCache.IssueTag(team, key)]);
                },
                context.RequestAborted);
            var linkTypes = await cache.GetOrCreateAsync(
                JiraCache.Key(team, "linktypes", ""),
                OptionsTtl,
                async () => {
                    var types = await client.GetLinkTypesAsync(team, CancellationToken.None);
                    return (types, 256L * types.Count, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team)]);
                },
                context.RequestAborted);
            var status = detail.Issue.Fields?.Status?.Name ?? "";
            var transitions = await cache.GetOrCreateAsync(
                JiraCache.Key(team, "transitions", $"{key}|{status}"),
                TransitionsTtl,
                async () => {
                    var found = await client.GetTransitionsAsync(team, key, CancellationToken.None);
                    return (found, 256L * found.Count, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team), JiraCache.IssueTag(team, key)]);
                },
                context.RequestAborted);
            return Ok(
                new JiraOptionsView(
                    [.. (editMeta.Fields?.Issuetype?.AllowedValues ?? []).Where(t => t.Id is not null).Select(t => new JiraNamedView(t.Id!, t.Name))],
                    [.. (editMeta.Fields?.Priority?.AllowedValues ?? []).Where(p => p.Id is not null).Select(p => new JiraNamedView(p.Id!, p.Name))],
                    [.. linkTypes.Where(t => t.Id is not null).Select(t => new JiraLinkTypeView(t.Id!, t.Name, t.Inward, t.Outward))],
                    [.. transitions.Where(t => t.Id is not null).Select(t => new JiraTransitionView(t.Id!, t.Name, t.To?.Name, t.To?.StatusCategory?.Key))]),
                JiraHttpJsonContext.Default.JiraOptionsView);
        });
    }

    private static async Task<IResult> AssignableAsync(
        HttpContext context,
        Guid teamId,
        string key,
        string? q,
        JiraGate gate,
        JiraClient client,
        JiraCache cache) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.Subscribe, gate);
        if (refusal is not null) {
            return refusal;
        }
        var query = q?.Trim() ?? "";
        if (query.Length > 100) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["q"] = "too_long" });
        }
        return await GuardAsync(async () => {
            var team = call!.Team;
            var users = await cache.GetOrCreateAsync(
                JiraCache.Key(team, "assignable", $"{key}|{query}"),
                AssignableTtl,
                async () => {
                    var found = await client.SearchAssignableAsync(team, key, query, CancellationToken.None);
                    return (found, 128L * found.Count + 64, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team)]);
                },
                context.RequestAborted);
            return Ok(
                new JiraAssignableView([.. users.Where(u => u.AccountId is not null).Select(u => new JiraUserView(u.AccountId, u.DisplayName))]),
                JiraHttpJsonContext.Default.JiraAssignableView);
        });
    }

    private static async Task<IResult> AttachmentAsync(
        HttpContext context,
        Guid teamId,
        string key,
        string attachmentId,
        JiraGate gate,
        JiraClient client,
        IslandStore islands) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.Subscribe, gate);
        if (refusal is not null) {
            return refusal;
        }
        return await GuardAsync(async () => {
            // Only an attachment of the issue being shown may be fetched, so a member cannot read any id on the site.
            var attachments = await islands.AttachmentsAsync(call!.Team, key, context.RequestAborted);
            if (!attachments.Any(attachment => attachment.Id == attachmentId)) {
                return JiraProblem(JiraError.NotFound);
            }
            var file = await client.GetAttachmentAsync(call.Team, attachmentId, context.RequestAborted);
            var headers = context.Response.Headers;
            headers.CacheControl = "private, max-age=300";
            headers.ContentSecurityPolicy = "sandbox";
            headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(file.Content, file.ContentType);
        });
    }

    // Issue writes.

    private static async Task<IResult> PatchIssueAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraIssuePatch body,
        JiraGate gate,
        JiraClient client,
        IslandStore islands,
        JiraWrites writes) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var summary = body.Summary is null ? null : CheckSummary(body.Summary, fields);
        var assignee = body.AssigneeAccountId.ValueKind switch {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => new JiraAssignee(null),
            JsonValueKind.String when AccountId().IsMatch(body.AssigneeAccountId.GetString()!) => new JiraAssignee(body.AssigneeAccountId.GetString()),
            _ => Invalid<JiraAssignee?>(fields, "assigneeAccountId", null),
        };
        if (body.PriorityId is { } priority && !NumericId().IsMatch(priority)) {
            fields["priorityId"] = "invalid";
        }
        if (body.IssueTypeId is { } issueType && !NumericId().IsMatch(issueType)) {
            fields["issueTypeId"] = "invalid";
        }
        Doc? description = null;
        if (body.Description is { } markup) {
            if (Blank(body.DescriptionBase)) {
                fields["descriptionBase"] = "required";
            }
            if (Blank(markup)) {
                description = new Doc([]);
            } else {
                var check = MarkupLimits.Check(markup, MarkupField.JiraDescription);
                description = check.Doc ?? Invalid<Doc?>(fields, "description", null, ErrorCode(check.Error));
            }
        }
        var rich = RichDescription(body, fields);
        if (summary is null && assignee is null && body.PriorityId is null && body.IssueTypeId is null && body.Description is null
            && rich is null && fields.Count == 0) {
            fields["body"] = "empty";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }

        return await GuardAsync(async () => {
            var team = call!.Team;
            JsonElement? adf = null;
            if (description is not null) {
                // The edit started from descriptionBase; anything Jira changed since, or an island the server does not
                // hold, refuses the write rather than lose content (no lossy write ever).
                var set = await islands.IslandsAsync(team, key, body.DescriptionBase!, context.RequestAborted);
                if (set is null || !set.Editable || !MarkupToAdf.TryWrite(description, set.Islands, out var written, out _)) {
                    return await StaleAsync(team, key, islands, context.RequestAborted);
                }
                adf = Parse(written);
            }
            if (rich is not null) {
                var (richAdf, refused) = await WriteRichAsync(team, key, body.DescriptionBase!, rich, islands, context.RequestAborted);
                if (refused is not null) {
                    return refused;
                }
                adf = richAdf;
            }
            await client.UpdateIssueAsync(team, key, new JiraIssueUpdate(summary, adf, assignee, body.PriorityId, body.IssueTypeId), context.RequestAborted);
            await writes.DoneAsync(call, key, JiraAudit.Update);
            var detail = await islands.GetAsync(team, key, context.RequestAborted);
            return Ok(IssueView(detail), JiraHttpJsonContext.Default.JiraIssueView);
        });
    }

    private static async Task<IResult> TransitionAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraTransitionRequest body,
        JiraGate gate,
        JiraClient client,
        JiraWrites writes) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        if (body.TransitionId is not { } transition || !NumericId().IsMatch(transition)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["transitionId"] = "invalid" });
        }
        return await GuardAsync(async () => {
            await client.TransitionAsync(call!.Team, key, transition, context.RequestAborted);
            await writes.DoneAsync(call, key, JiraAudit.Transition);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> PointsAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraPointsRequest body,
        JiraGate gate,
        JiraClient client,
        JiraWrites writes) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        decimal? points = body.Points.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.Number when body.Points.TryGetDecimal(out var value) && value is >= 0 and <= MaxPoints && value.Scale <= 2 => value,
            _ => -1,
        };
        if (points < 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["points"] = "invalid" });
        }
        return await GuardAsync(async () => {
            await client.SetPointsAsync(call!.Team, key, points, context.RequestAborted);
            await writes.DoneAsync(call, key, JiraAudit.Points);
            return Ok(new JiraPointsView(key, points), JiraHttpJsonContext.Default.JiraPointsView);
        });
    }

    private static async Task<IResult> SprintAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraSprintRequest body,
        JiraGate gate,
        JiraClient client,
        JiraWrites writes) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        long? sprint = body.SprintId.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.Number when body.SprintId.TryGetInt64(out var id) && id > 0 => id,
            _ => -1,
        };
        if (sprint < 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["sprintId"] = "invalid" });
        }
        return await GuardAsync(async () => {
            if (sprint is { } id) {
                await client.MoveToSprintAsync(call!.Team, id, [key], context.RequestAborted);
            } else if (call!.RequiredConfig.BoardId is { } board) {
                await client.MoveToBacklogAsync(call.Team, board, [key], context.RequestAborted);
            } else {
                return JiraProblem(JiraError.NotConfigured);
            }
            await writes.DoneAsync(call, key, JiraAudit.Sprint);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> AddCommentAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraCommentRequest body,
        JiraGate gate,
        JiraClient client,
        JiraWrites writes,
        TimeProvider time) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        var check = MarkupLimits.Check(body.Body ?? "", MarkupField.JiraText);
        if (check.Doc is not { } doc) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["body"] = ErrorCode(check.Error) });
        }
        // The comment is posted with the team's Jira account, so it names who wrote it; the name is the server's.
        var attributed = new Doc([new DocParagraph([new DocText($"Sent from Retroscope by {call!.Who.DisplayName}")]), .. doc.Blocks]);
        var adf = Parse(MarkupToAdf.Write(attributed));
        return await GuardAsync(async () => {
            var id = await client.AddCommentAsync(call.Team, key, adf, context.RequestAborted);
            await writes.DoneAsync(call, key, JiraAudit.Comment);
            var created = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            return Results.Json(
                new JiraCommentView(id ?? "", null, created, AdfToDoc.Render(adf, [])),
                JiraHttpJsonContext.Default.JiraCommentView,
                statusCode: StatusCodes.Status201Created);
        });
    }

    private static async Task<IResult> CreateLinkAsync(
        HttpContext context,
        Guid teamId,
        string key,
        JiraLinkCreateRequest body,
        JiraGate gate,
        JiraClient client,
        JiraCache cache,
        IslandStore islands,
        JiraWrites writes) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.TypeId is not { } typeId || !NumericId().IsMatch(typeId)) {
            fields["typeId"] = "invalid";
        }
        var target = body.TargetKey?.Trim().ToUpperInvariant();
        if (!JiraMap.IsIssueKey(target) || target == key) {
            fields["targetKey"] = "invalid";
        }
        var outward = body.Direction is null or "outward";
        if (!outward && body.Direction != "inward") {
            fields["direction"] = "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        return await GuardAsync(async () => {
            var team = call!.Team;
            var types = await cache.GetOrCreateAsync(
                JiraCache.Key(team, "linktypes", ""),
                OptionsTtl,
                async () => {
                    var found = await client.GetLinkTypesAsync(team, CancellationToken.None);
                    return (found, 256L * found.Count, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team)]);
                },
                context.RequestAborted);
            if (types.FirstOrDefault(type => type.Id == body.TypeId) is not { Name: { } name } type) {
                return Problem.Result(StatusCodes.Status422UnprocessableEntity, JiraErrorCode.Validation, new Dictionary<string, string> { ["typeId"] = "Unknown link type." });
            }
            // Parity with the original: "outward" makes this issue the link's outward side.
            await client.CreateLinkAsync(team, name, inwardKey: outward ? target! : key, outwardKey: outward ? key : target!, context.RequestAborted);
            await writes.DoneAsync(call, key, JiraAudit.LinkCreate);
            writes.Touched(team, target!);
            var detail = await islands.GetAsync(team, key, context.RequestAborted);
            var link = (detail.Issue.Fields?.Issuelinks ?? [])
                .Where(l => l.Type?.Id == body.TypeId && (l.InwardIssue?.Key == target || l.OutwardIssue?.Key == target))
                .OrderByDescending(l => long.TryParse(l.Id, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Select(LinkView)
                .FirstOrDefault()
                ?? new JiraLinkView(null, name, outward ? type.Outward : type.Inward, outward ? "outward" : "inward", target, null, null);
            return Results.Json(link, JiraHttpJsonContext.Default.JiraLinkView, statusCode: StatusCodes.Status201Created);
        });
    }

    private static async Task<IResult> DeleteLinkAsync(
        HttpContext context,
        Guid teamId,
        string key,
        string linkId,
        JiraGate gate,
        JiraClient client,
        JiraCache cache,
        IslandStore islands,
        JiraWrites writes,
        JiraAudit audit) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        if (!NumericId().IsMatch(linkId)) {
            return JiraProblem(JiraError.NotFound);
        }
        return await GuardAsync(async () => {
            var team = call!.Team;
            // Only a link of this issue, as Jira lists it now, may be deleted; a cached list could miss a newer link.
            cache.InvalidateTag(JiraCache.IssueTag(team, key));
            var detail = await islands.GetAsync(team, key, context.RequestAborted);
            if ((detail.Issue.Fields?.Issuelinks ?? []).FirstOrDefault(l => l.Id == linkId) is not { } link) {
                return JiraProblem(JiraError.NotFound);
            }
            await client.DeleteLinkAsync(team, linkId, context.RequestAborted);
            writes.Touched(team, key);
            if ((link.InwardIssue ?? link.OutwardIssue)?.Key is { } other) {
                writes.Touched(team, other);
            }
            await audit.WriteAsync(team, call.Who.Id, JiraAudit.LinkDelete, linkId, CancellationToken.None);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        Guid teamId,
        JiraCreateRequest body,
        JiraGate gate,
        JiraClient client,
        JiraCache cache,
        JiraAudit audit) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.JiraEditIssues, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var summary = CheckSummary(body.Summary ?? "", fields);
        var cloneFrom = body.CloneFrom?.Trim().ToUpperInvariant();
        if (cloneFrom is not null && !JiraMap.IsIssueKey(cloneFrom)) {
            fields["cloneFrom"] = "invalid";
        }
        if (body.ProjectKey is { } project && !ProjectKey().IsMatch(project)) {
            fields["projectKey"] = "invalid";
        }
        if (body.IssueTypeId is { } issueType && !NumericId().IsMatch(issueType)) {
            fields["issueTypeId"] = "invalid";
        }
        JsonElement? description = null;
        if (cloneFrom is null && !Blank(body.Description)) {
            var check = MarkupLimits.Check(body.Description!, body.FromRetro == true ? MarkupField.ActionItem : MarkupField.JiraText);
            if (check.Doc is { } doc) {
                description = Parse(MarkupToAdf.Write(doc));
            } else {
                fields["description"] = ErrorCode(check.Error);
            }
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        return await GuardAsync(async () => {
            var team = call!.Team;
            var config = call.RequiredConfig;
            var projectKey = body.ProjectKey;
            var issueTypeId = body.IssueTypeId;
            if (cloneFrom is not null) {
                var source = await client.GetIssueAsync(team, cloneFrom, ["description", "issuetype", "project"], context.RequestAborted);
                description = Parse(WithFooter(source.Fields?.Description, $"Cloned from {cloneFrom}"));
                issueTypeId ??= source.Fields?.Issuetype?.Id;
                projectKey ??= source.Fields?.Project?.Key;
            }
            projectKey ??= config.ProjectKey ?? config.TicketPrefix;
            if (projectKey is null) {
                return Problem.Result(StatusCodes.Status422UnprocessableEntity, JiraErrorCode.Validation, new Dictionary<string, string> { ["projectKey"] = "No project is configured." });
            }
            var created = await client.CreateIssueAsync(team, new JiraIssueCreate(projectKey, summary!, description, issueTypeId), context.RequestAborted);
            if (created.Key is null) {
                return JiraProblem(JiraError.Unavailable);
            }
            // A new issue may belong in browse results that were cached without it.
            cache.InvalidateTag(BrowseService.BrowseTag(team));
            await audit.WriteAsync(team, call.Who.Id, JiraAudit.Create, created.Key, CancellationToken.None);
            return Results.Json(new JiraCreatedView(created.Key), JiraHttpJsonContext.Default.JiraCreatedView, statusCode: StatusCodes.Status201Created);
        });
    }

    // Helpers.

    private static async Task<(JiraCall? Call, IResult? Refusal)> GateIssueAsync(
        HttpContext context,
        Guid teamId,
        string key,
        Capability capability,
        JiraGate gate) {
        var checkedCall = await gate.CheckAsync(context, teamId, capability, needsConfig: true);
        if (checkedCall.Refusal is null && !JiraMap.IsIssueKey(key)) {
            return (null, JiraProblem(JiraError.NotFound));
        }
        return checkedCall;
    }

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> run) {
        try {
            return await run();
        } catch (JiraException e) {
            return JiraProblem(e.Error);
        }
    }

    public static IResult JiraProblem(JiraError error, JiraStaleCurrent? current = null) => new JiraProblemResult(error, current);

    // Settings saves and tests answer an authentication failure with the scoped-token hint (F24).
    private static IResult ProbeProblem(JiraError error) => error.Code switch {
        JiraErrorCode.AuthFailed => JiraProblem(error with { Fields = new Dictionary<string, string> { ["token"] = ScopedTokenHint } }),
        JiraErrorCode.NotFound => JiraProblem(JiraError.Validation(new Dictionary<string, string> { ["boardId"] = "Jira has no such board." })),
        _ => JiraProblem(error),
    };

    private static async Task<IResult> StaleAsync(TeamId team, string key, IslandStore islands, CancellationToken cancellationToken) {
        var current = await islands.GetAsync(team, key, cancellationToken);
        return JiraProblem(new JiraError("stale", StatusCodes.Status409Conflict), new JiraStaleCurrent(current.Updated));
    }

    private static async Task<JiraTeamConfig?> SettingsChangedAsync(
        TeamId team,
        Credentials credentials,
        JiraCache cache,
        NpgsqlDataSource dataSource,
        ActorRegistry registry,
        CancellationToken cancellationToken) {
        credentials.Invalidate(team);
        cache.InvalidateTag(JiraCache.TeamTag(team));
        var config = await credentials.LoadAsync(team, cancellationToken);
        var settings = config is null
            ? TeamJiraSettings.None
            : new TeamJiraSettings(true, config.TicketPrefix, config.MembersCanEdit);
        await using var command = dataSource.CreateCommand("select id from poker_sessions where team_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            // Dropped for sessions that are not live; they read the row when they next load.
            registry.Post<PokerState, PokerInput>(RoomKey.Poker(new SessionId(reader.GetGuid(0))), new JiraSettingsChanged(settings));
        }
        return config;
    }

    private static JiraConfigView ConfigView(JiraTeamConfig? config, bool isAdmin) => config is null
        ? new JiraConfigView(false, null, null, null, null, null, null, true, Credentials.StatusText(JiraConfigStatus.Ok), null, null, null)
        : new JiraConfigView(
            true,
            config.SiteHost,
            config.ProjectKey,
            config.BoardId,
            config.TicketPrefix,
            config.PointsFieldId,
            config.SprintFieldId,
            config.MembersCanEdit,
            Credentials.StatusText(config.Status),
            config.LastCheckedAt?.ToUnixTimeMilliseconds(),
            isAdmin ? config.EmailMasked : null,
            isAdmin ? config.TokenLast4 : null);

    /// <summary>The stored email and token, for a save that keeps one of them.</summary>
    private static (string Email, string Token)? OpenStored(Credentials credentials, JiraTeamConfig config) {
        if (credentials.TryCreateBasicParameter(config.Team, config.CredentialsBox) is not { } parameter) {
            return null;
        }
        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        // An Atlassian account email holds no colon, so the first one ends it.
        var colon = pair.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? null : (pair[..colon], pair[(colon + 1)..]);
    }

    private static JiraRowView RowView(IssueRow row, string? roundState) => new(
        row.Key,
        row.Summary,
        row.IssueType,
        row.Status,
        row.StatusCategory,
        row.Priority,
        row.Assignee,
        row.Points,
        row.ParentKey,
        row.ParentSummary,
        roundState);

    private static JiraIssueView IssueView(IssueDetail detail) {
        var issue = detail.Issue;
        var fields = issue.Fields;
        var sprint = JiraMap.ReadSprint(issue, detail.Config.SprintFieldId);
        var description = detail.Description;
        var islandIds = description.Doc.Blocks.OfType<DocIsland>().Select(island => island.Id).Distinct(StringComparer.Ordinal).ToList();
        return new JiraIssueView(
            issue.Key ?? "",
            detail.Updated,
            new JiraIssueFieldsView(
                fields?.Summary,
                fields?.Status?.Name,
                fields?.Status?.StatusCategory?.Key,
                fields?.Priority?.Name,
                fields?.Priority?.Id,
                fields?.Issuetype?.Name,
                fields?.Issuetype?.Id,
                UserView(fields?.Assignee),
                UserView(fields?.Reporter),
                fields?.Parent?.Key is { } parent ? new JiraParentView(parent, fields.Parent.Fields?.Summary) : null,
                fields?.Labels ?? [],
                JiraMap.ReadPoints(issue, detail.Config.PointsFieldId),
                sprint is null ? null : new JiraSprintView(sprint.Id, sprint.Name, null, sprint.StartDate),
                fields?.Created,
                fields?.Project?.Key),
            description.Doc,
            description.Editable,
            description.Editable ? MarkupWriter.Write(description.Doc) : null,
            islandIds,
            [.. detail.Comments.Select(CommentView)],
            detail.CommentTotal,
            [.. (fields?.Attachment ?? []).Where(a => a.Id is not null).Select(a => new JiraAttachmentView(a.Id!, a.Filename, a.MimeType, a.Size))],
            [.. (fields?.Issuelinks ?? []).Select(LinkView)]);
    }

    private static JiraUserView? UserView(JiraUser? user) => user is null ? null : new JiraUserView(user.AccountId, user.DisplayName);

    private static JiraCommentView CommentView(RenderedComment comment) => new(comment.Id, comment.Author, comment.Created, comment.Body);

    // A link lists the other issue on the side it sits: an outwardIssue is what this issue "blocks", an inwardIssue
    // what it "is blocked by".
    private static JiraLinkView LinkView(JiraIssueLink link) {
        var outward = link.OutwardIssue is not null;
        var other = link.OutwardIssue ?? link.InwardIssue;
        return new JiraLinkView(
            link.Id,
            link.Type?.Name,
            outward ? link.Type?.Outward : link.Type?.Inward,
            outward ? "outward" : "inward",
            other?.Key,
            other?.Fields?.Summary,
            other?.Fields?.Status?.Name);
    }

    // The source description with a footer paragraph; its blocks (islands included) are copied byte for byte.
    private static string WithFooter(JsonElement? adf, string footer) {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("type", "doc");
            json.WriteNumber("version", 1);
            json.WriteStartArray("content");
            if (adf is { ValueKind: JsonValueKind.Object } doc
                && doc.TryGetProperty("type", out var type) && type.ValueEquals("doc")
                && doc.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) {
                foreach (var block in content.EnumerateArray()) {
                    json.WriteRawValue(block.GetRawText());
                }
            }
            json.WriteStartObject();
            json.WriteString("type", "paragraph");
            json.WriteStartArray("content");
            json.WriteStartObject();
            json.WriteString("type", "text");
            json.WriteString("text", footer);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static JsonElement Parse(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? CheckSummary(string value, Dictionary<string, string> fields) {
        var summary = value.Trim();
        if (summary.Length == 0) {
            fields["summary"] = "required";
        } else if (summary.Length > MaxSummaryLength || summary.Any(char.IsControl)) {
            fields["summary"] = "invalid";
        }
        return summary;
    }

    private static BrowseStatus? ParseStatus(string? value) => value switch {
        null or "" or "all" => BrowseStatus.All,
        "not_done" or "not-done" => BrowseStatus.NotDone,
        "new" => BrowseStatus.New,
        "indeterminate" => BrowseStatus.InProgress,
        "done" => BrowseStatus.Done,
        _ => null,
    };

    private static BrowsePoints? ParsePoints(string? value) => value switch {
        null or "" or "any" => BrowsePoints.Any,
        "unpointed" or "unestimated" => new BrowsePoints(BrowsePointsKind.Unpointed),
        "pointed" => new BrowsePoints(BrowsePointsKind.Pointed),
        _ when decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number <= MaxPoints =>
            new BrowsePoints(BrowsePointsKind.Exactly, number),
        _ => null,
    };

    private static BrowseScope? ParseScope(string? value) => value switch {
        null or "" or "board" => BrowseScope.Board,
        "open" => BrowseScope.Open,
        "all" => BrowseScope.All,
        _ => null,
    };

    private static T Invalid<T>(Dictionary<string, string> fields, string name, T fallback, string code = "invalid") {
        fields[name] = code;
        return fallback;
    }

    private static string ErrorCode(MarkupError? error) => error switch {
        MarkupError.Empty => "required",
        MarkupError.TooLong => "too_long",
        MarkupError.TooManyImages => "too_many_images",
        _ => "invalid",
    };

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static IResult Ok<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => Results.Json(value, typeInfo);

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectKey();

    [GeneratedRegex(@"^[0-9]{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericId();

    [GeneratedRegex(@"^[A-Za-z0-9:_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountId();

    private sealed class JiraProblemResult(JiraError error, JiraStaleCurrent? current) : IResult {
        public Task ExecuteAsync(HttpContext httpContext) {
            var response = httpContext.Response;
            var retryAfter = error.RetryAfter is { } wait ? (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds)) : (int?)null;
            if (retryAfter is { } seconds) {
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }
            var shape = Problem.Body(error.HttpStatus, error.Code);
            response.StatusCode = error.HttpStatus;
            response.ContentType = Problem.ContentType;
            return JsonSerializer.SerializeAsync(
                response.Body,
                new JiraProblemBody(shape.Type, shape.Title, shape.Status, shape.Code, error.Fields, retryAfter, current),
                JiraHttpJsonContext.Default.JiraProblemBody,
                httpContext.RequestAborted);
        }
    }
}

/// <summary>
/// What every write to an issue does after Jira accepted it: drop the cached entries that show the issue, have the
/// team's live poker rooms fetch it again (R15b), and record the audit row.
/// </summary>
public sealed class JiraWrites(JiraCache cache, PokerJiraLoop loop, JiraAudit audit) {
    public async Task DoneAsync(JiraCall call, string issueKey, string action) {
        Touched(call.Team, issueKey);
        await audit.WriteAsync(call.Team, call.Who.Id, action, issueKey, CancellationToken.None);
    }

    /// <summary>The cache first: the rooms' refetch must not read the entry the write made stale.</summary>
    public void Touched(TeamId team, string issueKey) {
        cache.InvalidateTag(JiraCache.IssueTag(team, issueKey));
        loop.Invalidate(team, issueKey);
    }
}
