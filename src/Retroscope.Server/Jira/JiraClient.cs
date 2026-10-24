using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Server.Json;

namespace Retroscope.Server.Jira;

/// <summary>Resolves a host name for the Jira handler, which then vets every address itself.</summary>
public interface IJiraResolver {
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsJiraResolver : IJiraResolver {
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Transport settings. The defaults are production's; only tests change the scheme, port or address rule.</summary>
public sealed record JiraClientOptions {
    public string Scheme { get; init; } = "https";

    public int Port { get; init; } = 443;

    public IJiraResolver Resolver { get; init; } = new DnsJiraResolver();

    public Func<IPAddress, bool> IsAllowedAddress { get; init; } = IpClassifier.IsPublic;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public long MaxResponseBytes { get; init; } = 4L * 1024 * 1024;

    public long MaxAttachmentBytes { get; init; } = 5L * 1024 * 1024;

    public int MaxRetries { get; init; } = 2;

    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Uniform in [0, 1), for full-jitter backoff.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}

public sealed record JiraAttachment(string ContentType, byte[] Content);

/// <summary>Set means "change the assignee"; a null account id unassigns.</summary>
public sealed record JiraAssignee(string? AccountId);

public sealed record JiraIssueUpdate(
    string? Summary = null,
    JsonElement? Description = null,
    JiraAssignee? Assignee = null,
    string? PriorityId = null,
    string? IssueTypeId = null);

/// <param name="IssueTypeId">The type to create; when null, <paramref name="IssueTypeName"/> is sent instead.</param>
public sealed record JiraIssueCreate(
    string ProjectKey,
    string Summary,
    JsonElement? Description = null,
    string? IssueTypeId = null,
    string? IssueTypeName = null);

/// <summary>What a config save learns from Jira with credentials that are not stored yet.</summary>
public sealed record JiraProbeResult(JiraUser Myself, JiraFieldIds Fields, JiraBoard? Board);

/// <summary>
/// The only way the server talks to Jira (DESIGN §f). Every call goes through one <see cref="HttpClient"/> whose
/// handler resolves DNS itself, refuses non-public addresses and never follows redirects; through the team's
/// throttle and circuit breaker; with retries on 429/503 and connect failures; with a response size cap; and with
/// Jira's errors mapped to <see cref="JiraException"/>. Identical concurrent reads of one team share one request.
/// </summary>
public sealed partial class JiraClient : IDisposable {
    public const string MediaHost = "api.media.atlassian.com";

    private const int MaxSprintPages = 30;
    private const int SprintPageSize = 50;
    private const string ActiveState = "active";
    private const string ClosedState = "closed";

    private static readonly TimeSpan FieldsTtl = TimeSpan.FromHours(6);
    private static readonly string[] AttachmentTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    private readonly Credentials _credentials;
    private readonly JiraThrottle _throttle;
    private readonly JiraCache _cache;
    private readonly JiraHostPolicy _hostPolicy;
    private readonly JiraClientOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<JiraClient> _logger;
    private readonly HttpClient _http;
    private readonly Lock _inFlightGate = new();
    private readonly Dictionary<string, Task<JiraResponse>> _inFlight = new(StringComparer.Ordinal);

    public JiraClient(
        Credentials credentials,
        JiraThrottle throttle,
        JiraCache cache,
        JiraHostPolicy hostPolicy,
        JiraClientOptions options,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger<JiraClient> logger) {
        _credentials = credentials;
        _throttle = throttle;
        _cache = cache;
        _hostPolicy = hostPolicy;
        _options = options;
        _time = time;
        _delay = delay;
        _logger = logger;
        _http = new HttpClient(CreateHandler(options), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => Keyring.Parse(
            configuration["APP_KEYS"] ?? throw new InvalidOperationException("APP_KEYS is not set.")));
        services.TryAddSingleton(_ => JiraHostPolicy.FromAllowlist(configuration["JIRA_HOST_ALLOWLIST"]));
        services.AddSingleton(sp => new Credentials(
            sp.GetRequiredService<Keyring>(), sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => {
            var time = sp.GetRequiredService<TimeProvider>();
            return new JiraThrottle(new JiraThrottleOptions(), time, (delay, token) => Task.Delay(delay, time, token));
        });
        services.AddSingleton(sp => new JiraCache(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => {
            var time = sp.GetRequiredService<TimeProvider>();
            return new JiraClient(
                sp.GetRequiredService<Credentials>(),
                sp.GetRequiredService<JiraThrottle>(),
                sp.GetRequiredService<JiraCache>(),
                sp.GetRequiredService<JiraHostPolicy>(),
                new JiraClientOptions(),
                time,
                (delay, token) => Task.Delay(delay, time, token),
                sp.GetRequiredService<ILogger<JiraClient>>());
        });
        services.AddHostedService<CredentialRotation>();
    }

    public static void Map(IEndpointRouteBuilder app) {
    }

    public void Dispose() => _http.Dispose();

    // Reads.

    public Task<JiraUser> GetMyselfAsync(TeamId team, CancellationToken cancellationToken) =>
        GetAsync(team, "/rest/api/3/myself", "/rest/api/3/myself", JiraClientJsonContext.Default.JiraUser, cancellationToken);

    /// <summary>
    /// Checks credentials that are not saved yet (config save): who they authenticate as, which fields hold points
    /// and sprints, and the Agile board (by id, else the project's first scrum board, else its first board).
    /// A 401 here changes no stored status.
    /// </summary>
    public async Task<JiraProbeResult> ProbeAsync(
        TeamId team,
        string siteHost,
        string email,
        string token,
        string? projectKey,
        long? boardId,
        CancellationToken cancellationToken) {
        if (!_hostPolicy.TryNormalize(siteHost, out var host)) {
            throw new JiraException(JiraError.Validation(new Dictionary<string, string> { ["siteHost"] = "This host is not allowed." }));
        }

        var target = new JiraTarget(team, host, null, Credentials.CreateBasicParameter(email, token), Saved: false);
        var myself = await SendAsync(target, Get("/rest/api/3/myself", "/rest/api/3/myself"), JiraClientJsonContext.Default.JiraUser, cancellationToken);
        var fields = await SendAsync(target, Get("/rest/api/3/field", "/rest/api/3/field"), JiraClientJsonContext.Default.IReadOnlyListJiraField, cancellationToken);
        JiraBoard? board = null;
        if (boardId is { } id) {
            board = await SendAsync(target, Get($"/rest/agile/1.0/board/{id}", "/rest/agile/1.0/board/{id}"), JiraClientJsonContext.Default.JiraBoard, cancellationToken);
        } else if (projectKey is not null) {
            var boards = await SendAsync(target, Get(
                $"/rest/agile/1.0/board?projectKeyOrId={Segment(RequireProjectKey(projectKey))}&maxResults=100", "/rest/agile/1.0/board?projectKeyOrId"),
                JiraClientJsonContext.Default.JiraValuesPageJiraBoard,
                cancellationToken);
            var values = boards.Values ?? [];
            board = values.FirstOrDefault(b => b.Type == "scrum") ?? (values.Count > 0 ? values[0] : null);
        }

        return new JiraProbeResult(myself, JiraMap.DiscoverFields(fields), board);
    }

    /// <summary>The site's field catalogue, cached for 6 hours (field discovery runs at config save).</summary>
    public Task<IReadOnlyList<JiraField>> GetFieldsAsync(TeamId team, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(JiraCache.Key(team, "fields", ""), FieldsTtl, async () => {
            var response = await FetchAsync(team, Get("/rest/api/3/field", "/rest/api/3/field"), CancellationToken.None);
            var fields = Deserialize(response, JiraClientJsonContext.Default.IReadOnlyListJiraField);
            return (fields, (long)response.Body.Length, (IReadOnlyCollection<string>?)[JiraCache.TeamTag(team)]);
        }, cancellationToken);

    public Task<JiraBoard> GetBoardAsync(TeamId team, long boardId, CancellationToken cancellationToken) =>
        GetAsync(team, $"/rest/agile/1.0/board/{boardId}", "/rest/agile/1.0/board/{id}", JiraClientJsonContext.Default.JiraBoard, cancellationToken);

    /// <summary>The board's active and future sprints, every page, in bucket order (start date, then natural name order).</summary>
    public async Task<IReadOnlyList<JiraSprint>> GetSprintsAsync(TeamId team, long boardId, CancellationToken cancellationToken) {
        const int maxPages = 30;
        var sprints = new List<JiraSprint>();
        var startAt = 0;
        for (var page = 0; page < maxPages; page++) {
            var result = await GetAsync(
                team,
                $"/rest/agile/1.0/board/{boardId}/sprint?state=active,future&startAt={startAt}&maxResults=50",
                "/rest/agile/1.0/board/{id}/sprint",
                JiraClientJsonContext.Default.JiraValuesPageJiraSprint,
                cancellationToken);
            var values = result.Values ?? [];
            sprints.AddRange(values);
            if (result.IsLast || values.Count == 0) {
                break;
            }
            startAt += values.Count;
        }

        sprints.Sort(JiraMap.SprintOrder);
        return sprints;
    }

    /// <summary>
    /// The board's active sprints, then its newest <paramref name="keep"/> closed ones in Jira's order (oldest first).
    /// Jira lists closed sprints oldest first, so when it sends a total the read jumps to the tail: a young board costs
    /// two calls and an old one three. Without a total, or when a stale total jumps too far, it pages forward keeping the
    /// last <paramref name="keep"/>. At most 30 calls in all.
    /// </summary>
    public async Task<IReadOnlyList<JiraSprint>> GetRecentSprintsAsync(TeamId team, long boardId, int keep, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        var budget = MaxSprintPages;

        Task<JiraValuesPage<JiraSprint>> PageAsync(string state, int startAt) {
            budget--;
            return GetAsync(
                team,
                $"/rest/agile/1.0/board/{boardId}/sprint?state={state}&startAt={startAt}&maxResults={SprintPageSize}",
                "/rest/agile/1.0/board/{id}/sprint",
                JiraClientJsonContext.Default.JiraValuesPageJiraSprint,
                cancellationToken);
        }

        // Pages forward from startAt into ring, keeping its last `keep`; returns how many sprints were read.
        async Task<int> WalkAsync(int startAt, List<JiraSprint> ring) {
            var read = 0;
            while (budget > 0) {
                var page = await PageAsync(ClosedState, startAt);
                var values = page.Values ?? [];
                read += values.Count;
                KeepTail(ring, values, keep);
                if (page.IsLast || values.Count == 0) {
                    break;
                }
                startAt += values.Count;
            }
            return read;
        }

        var active = await PageAsync(ActiveState, 0);
        var first = await PageAsync(ClosedState, 0);
        var firstValues = first.Values ?? [];
        var closed = new List<JiraSprint>();
        KeepTail(closed, firstValues, keep);
        if (!first.IsLast && firstValues.Count > 0) {
            var jumpTo = first.Total is { } total ? total - keep : 0;
            var tail = new List<JiraSprint>();
            if (jumpTo > firstValues.Count && await WalkAsync(jumpTo, tail) >= keep) {
                closed = tail;
            } else {
                await WalkAsync(firstValues.Count, closed);
            }
        }

        return [.. active.Values ?? [], .. closed];
    }

    /// <summary>One page of a sprint's issues in rank order.</summary>
    public Task<JiraIssuePage> GetSprintIssuesAsync(
        TeamId team, long sprintId, string? jql, IReadOnlyList<string> fields, int startAt, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/agile/1.0/sprint/{sprintId}/issue?{IssuePageQuery(jql, fields, startAt)}",
            "/rest/agile/1.0/sprint/{id}/issue",
            JiraClientJsonContext.Default.JiraIssuePage,
            cancellationToken);

    /// <summary>One page of the board's backlog in rank order.</summary>
    public Task<JiraIssuePage> GetBacklogAsync(
        TeamId team, long boardId, string? jql, IReadOnlyList<string> fields, int startAt, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/agile/1.0/board/{boardId}/backlog?{IssuePageQuery(jql, fields, startAt)}",
            "/rest/agile/1.0/board/{id}/backlog",
            JiraClientJsonContext.Default.JiraIssuePage,
            cancellationToken);

    /// <summary>One page of <c>POST /rest/api/3/search/jql</c>; pass the previous page's <c>NextPageToken</c> until it is null or <c>IsLast</c>.</summary>
    public Task<JiraSearchPage> SearchAsync(
        TeamId team, string jql, IReadOnlyList<string> fields, int maxResults, string? nextPageToken, CancellationToken cancellationToken) {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new JiraSearchRequest(jql, fields, maxResults, nextPageToken), JiraClientJsonContext.Default.JiraSearchRequest);
        var request = new JiraRequest(HttpMethod.Post, "/rest/api/3/search/jql", "/rest/api/3/search/jql", body, RetryOnResponse: true, Shared: true);
        return FetchAsync(team, request, JiraClientJsonContext.Default.JiraSearchPage, cancellationToken);
    }

    public Task<JiraIssue> GetIssueAsync(TeamId team, string issueKey, IReadOnlyList<string> fields, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/api/3/issue/{Segment(RequireKey(issueKey))}?fields={Query(string.Join(',', fields))}",
            "/rest/api/3/issue/{key}",
            JiraClientJsonContext.Default.JiraIssue,
            cancellationToken);

    /// <summary>Comments, newest first.</summary>
    public Task<JiraCommentPage> GetCommentsAsync(TeamId team, string issueKey, int startAt, int maxResults, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/api/3/issue/{Segment(RequireKey(issueKey))}/comment?orderBy=-created&startAt={startAt}&maxResults={maxResults}",
            "/rest/api/3/issue/{key}/comment",
            JiraClientJsonContext.Default.JiraCommentPage,
            cancellationToken);

    public Task<JiraEditMeta> GetEditMetaAsync(TeamId team, string issueKey, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/api/3/issue/{Segment(RequireKey(issueKey))}/editmeta",
            "/rest/api/3/issue/{key}/editmeta",
            JiraClientJsonContext.Default.JiraEditMeta,
            cancellationToken);

    public async Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(TeamId team, string issueKey, CancellationToken cancellationToken) {
        var result = await GetAsync(
            team,
            $"/rest/api/3/issue/{Segment(RequireKey(issueKey))}/transitions",
            "/rest/api/3/issue/{key}/transitions",
            JiraClientJsonContext.Default.JiraTransitions,
            cancellationToken);
        return result.Transitions ?? [];
    }

    public Task<JiraProject> GetProjectAsync(TeamId team, string projectKey, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/api/3/project/{Segment(RequireProjectKey(projectKey))}",
            "/rest/api/3/project/{key}",
            JiraClientJsonContext.Default.JiraProject,
            cancellationToken);

    public async Task<IReadOnlyList<JiraLinkType>> GetLinkTypesAsync(TeamId team, CancellationToken cancellationToken) {
        var result = await GetAsync(team, "/rest/api/3/issueLinkType", "/rest/api/3/issueLinkType", JiraClientJsonContext.Default.JiraLinkTypes, cancellationToken);
        return result.IssueLinkTypes ?? [];
    }

    public Task<IReadOnlyList<JiraUser>> SearchAssignableAsync(TeamId team, string issueKey, string query, CancellationToken cancellationToken) =>
        GetAsync(
            team,
            $"/rest/api/3/user/assignable/search?issueKey={Query(RequireKey(issueKey))}&maxResults=25&query={Query(query)}",
            "/rest/api/3/user/assignable/search",
            JiraClientJsonContext.Default.IReadOnlyListJiraUser,
            cancellationToken);

    /// <summary>Issue types a project can create (the successor of the deprecated <c>/issue/createmeta</c>).</summary>
    public async Task<IReadOnlyList<JiraIssueType>> GetCreateIssueTypesAsync(TeamId team, string projectKey, CancellationToken cancellationToken) {
        var result = await GetAsync(
            team,
            $"/rest/api/3/issue/createmeta/{Segment(RequireProjectKey(projectKey))}/issuetypes?maxResults=200",
            "/rest/api/3/issue/createmeta/{key}/issuetypes",
            JiraClientJsonContext.Default.JiraCreateMetaIssueTypes,
            cancellationToken);
        return result.IssueTypes ?? [];
    }

    /// <summary>
    /// An image attachment's bytes, asked for with <c>redirect=false</c>. If Jira redirects anyway, one hop is followed,
    /// only to the Atlassian media host or an <c>*.atlassian.net</c> host, without the Authorization header and through
    /// the same address vetting. Only PNG, JPEG, GIF and WebP up to 5 MiB are returned (never SVG). The caller checks
    /// that the id belongs to the issue being shown.
    /// </summary>
    public Task<JiraAttachment> GetAttachmentAsync(TeamId team, string attachmentId, CancellationToken cancellationToken) {
        if (!NumericId().IsMatch(attachmentId)) {
            throw new ArgumentException("Attachment ids are numeric.", nameof(attachmentId));
        }

        var request = Get($"/rest/api/3/attachment/content/{attachmentId}?redirect=false", "/rest/api/3/attachment/content/{id}") with {
            Shared = false,
            Attachment = true,
        };
        return FetchAttachmentAsync(team, request, cancellationToken);
    }

    // Writes. Only PUT and DELETE are retried after a response; POSTs are retried on connect failure only.

    public Task UpdateIssueAsync(TeamId team, string issueKey, JiraIssueUpdate update, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(update);
        var body = WriteJson(writer => {
            writer.WriteStartObject();
            writer.WriteStartObject("fields"u8);
            if (update.Summary is not null) {
                writer.WriteString("summary"u8, update.Summary);
            }
            if (update.Description is { } description) {
                // The description's own bytes: opaque parts of a rich save reach Jira exactly as Jira wrote them.
                writer.WritePropertyName("description"u8);
                writer.WriteRawValue(description.GetRawText());
            }
            if (update.Assignee is { } assignee) {
                writer.WritePropertyName("assignee"u8);
                WriteAccount(writer, assignee.AccountId);
            }
            if (update.PriorityId is not null) {
                WriteIdRef(writer, "priority"u8, update.PriorityId);
            }
            if (update.IssueTypeId is not null) {
                WriteIdRef(writer, "issuetype"u8, update.IssueTypeId);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        return PutIssueAsync(team, issueKey, body, cancellationToken);
    }

    /// <summary>Writes points through the team's configured points field only; null clears it. Decimals are kept.</summary>
    public async Task SetPointsAsync(TeamId team, string issueKey, decimal? points, CancellationToken cancellationToken) {
        var config = await ConfigAsync(team, cancellationToken);
        if (config.PointsFieldId is not { } fieldId || !JiraMap.IsCustomFieldId(fieldId)) {
            throw new JiraException(JiraError.NotConfigured);
        }

        var body = WriteJson(writer => {
            writer.WriteStartObject();
            writer.WriteStartObject("fields"u8);
            if (points is { } value) {
                writer.WriteNumber(fieldId, value);
            } else {
                writer.WriteNull(fieldId);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        await PutIssueAsync(team, issueKey, body, cancellationToken);
    }

    public Task TransitionAsync(TeamId team, string issueKey, string transitionId, CancellationToken cancellationToken) {
        RequireNumeric(transitionId, nameof(transitionId));
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new JiraTransitionRequest(new JiraIdRef(transitionId)), JiraClientJsonContext.Default.JiraTransitionRequest);
        return SendWriteAsync(team, Post($"/rest/api/3/issue/{Segment(RequireKey(issueKey))}/transitions", "/rest/api/3/issue/{key}/transitions", body), cancellationToken);
    }

    public Task MoveToSprintAsync(TeamId team, long sprintId, IReadOnlyList<string> issueKeys, CancellationToken cancellationToken) =>
        SendWriteAsync(team, Post($"/rest/agile/1.0/sprint/{sprintId}/issue", "/rest/agile/1.0/sprint/{id}/issue", MoveBody(issueKeys)), cancellationToken);

    public Task MoveToBacklogAsync(TeamId team, long boardId, IReadOnlyList<string> issueKeys, CancellationToken cancellationToken) =>
        SendWriteAsync(team, Post($"/rest/agile/1.0/backlog/{boardId}/issue", "/rest/agile/1.0/backlog/{id}/issue", MoveBody(issueKeys)), cancellationToken);

    /// <summary>Posts an ADF comment; returns the comment id.</summary>
    public async Task<string?> AddCommentAsync(TeamId team, string issueKey, JsonElement adfBody, CancellationToken cancellationToken) {
        var body = JsonSerializer.SerializeToUtf8Bytes(new JiraCommentRequest(adfBody), JiraClientJsonContext.Default.JiraCommentRequest);
        var created = await FetchAsync(
            team,
            Post($"/rest/api/3/issue/{Segment(RequireKey(issueKey))}/comment", "/rest/api/3/issue/{key}/comment", body),
            JiraClientJsonContext.Default.JiraCreatedComment,
            cancellationToken);
        return created.Id;
    }

    public Task CreateLinkAsync(TeamId team, string linkTypeName, string inwardKey, string outwardKey, CancellationToken cancellationToken) {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new JiraLinkRequest(new JiraNameRef(linkTypeName), new JiraKeyRef(RequireKey(inwardKey)), new JiraKeyRef(RequireKey(outwardKey))),
            JiraClientJsonContext.Default.JiraLinkRequest);
        return SendWriteAsync(team, Post("/rest/api/3/issueLink", "/rest/api/3/issueLink", body), cancellationToken);
    }

    public Task DeleteLinkAsync(TeamId team, string linkId, CancellationToken cancellationToken) {
        RequireNumeric(linkId, nameof(linkId));
        var request = new JiraRequest(HttpMethod.Delete, $"/rest/api/3/issueLink/{linkId}", "/rest/api/3/issueLink/{id}", null, RetryOnResponse: true, Shared: false);
        return SendWriteAsync(team, request, cancellationToken);
    }

    public Task<JiraCreatedIssue> CreateIssueAsync(TeamId team, JiraIssueCreate create, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(create);
        var body = WriteJson(writer => {
            writer.WriteStartObject();
            writer.WriteStartObject("fields"u8);
            writer.WriteStartObject("project"u8);
            writer.WriteString("key"u8, RequireProjectKey(create.ProjectKey));
            writer.WriteEndObject();
            writer.WriteStartObject("issuetype"u8);
            if (create.IssueTypeId is not null) {
                writer.WriteString("id"u8, create.IssueTypeId);
            } else {
                writer.WriteString("name"u8, create.IssueTypeName ?? "Story");
            }
            writer.WriteEndObject();
            writer.WriteString("summary"u8, create.Summary);
            if (create.Description is { } description) {
                writer.WritePropertyName("description"u8);
                description.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        return FetchAsync(team, Post("/rest/api/3/issue", "/rest/api/3/issue", body), JiraClientJsonContext.Default.JiraCreatedIssue, cancellationToken);
    }

    // Plumbing.

    private Task PutIssueAsync(TeamId team, string issueKey, byte[] body, CancellationToken cancellationToken) {
        var request = new JiraRequest(
            HttpMethod.Put, $"/rest/api/3/issue/{Segment(RequireKey(issueKey))}", "/rest/api/3/issue/{key}", body, RetryOnResponse: true, Shared: false);
        return SendWriteAsync(team, request, cancellationToken);
    }

    private async Task SendWriteAsync(TeamId team, JiraRequest request, CancellationToken cancellationToken) =>
        await FetchAsync(team, request, cancellationToken);

    private async Task<T> GetAsync<T>(TeamId team, string pathAndQuery, string template, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        Deserialize(await FetchAsync(team, Get(pathAndQuery, template), cancellationToken), typeInfo);

    private async Task<T> FetchAsync<T>(TeamId team, JiraRequest request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        Deserialize(await FetchAsync(team, request, cancellationToken), typeInfo);

    private async Task<T> SendAsync<T>(JiraTarget target, JiraRequest request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        Deserialize(await ExecuteAsync(target, request, cancellationToken).WaitAsync(cancellationToken), typeInfo);

    private async Task<JiraResponse> FetchAsync(TeamId team, JiraRequest request, CancellationToken cancellationToken) {
        _throttle.ThrowIfOpen(team);
        if (!request.Shared) {
            return await ExecuteAsync(await TargetAsync(team, cancellationToken), request, cancellationToken);
        }

        var key = $"{team.Value:N} {request.Method} {request.PathAndQuery} {BodyHash(request.Body)}";
        Task<JiraResponse> task;
        lock (_inFlightGate) {
            if (!_inFlight.TryGetValue(key, out task!)) {
                task = ExecuteSharedAsync(key, team, request);
                _inFlight[key] = task;
            }
        }

        return await task.WaitAsync(cancellationToken);
    }

    // Runs without any caller's token: a caller that gives up must not fail the others waiting on the same request.
    private async Task<JiraResponse> ExecuteSharedAsync(string key, TeamId team, JiraRequest request) {
        await Task.Yield();
        try {
            return await ExecuteAsync(await TargetAsync(team, CancellationToken.None), request, CancellationToken.None);
        } finally {
            lock (_inFlightGate) {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task<JiraAttachment> FetchAttachmentAsync(TeamId team, JiraRequest request, CancellationToken cancellationToken) {
        _throttle.ThrowIfOpen(team);
        var target = await TargetAsync(team, cancellationToken);
        var response = await ExecuteAsync(target, request, cancellationToken);
        return new JiraAttachment(response.ContentType!, response.Body);
    }

    private async Task<JiraTeamConfig> ConfigAsync(TeamId team, CancellationToken cancellationToken) =>
        await _credentials.LoadAsync(team, cancellationToken) ?? throw new JiraException(JiraError.NotConfigured);

    private async Task<JiraTarget> TargetAsync(TeamId team, CancellationToken cancellationToken) {
        var config = await ConfigAsync(team, cancellationToken);
        if (!_hostPolicy.TryNormalize(config.SiteHost, out var host) || host != config.SiteHost) {
            throw new JiraException(JiraError.NotConfigured);
        }

        return new JiraTarget(team, host, config.CredentialsBox, null, Saved: true);
    }

    private async Task<JiraResponse> ExecuteAsync(JiraTarget target, JiraRequest request, CancellationToken cancellationToken) {
        for (var attempt = 0; ; attempt++) {
            TimeSpan? retryIn;
            using (await _throttle.AcquireAsync(target.Team, cancellationToken)) {
                var outcome = await AttemptAsync(target, request, attempt, cancellationToken);
                if (outcome.Response is { } response) {
                    return response;
                }

                retryIn = attempt < _options.MaxRetries ? outcome.RetryIn : null;
                if (retryIn is null) {
                    if (outcome.Error == JiraError.AuthFailed && target.Saved) {
                        await MarkAuthFailedAsync(target.Team);
                    }
                    throw new JiraException(outcome.Error!, outcome.Exception);
                }
            }

            await _delay(retryIn.Value, cancellationToken);
        }
    }

    private async Task<Outcome> AttemptAsync(JiraTarget target, JiraRequest request, int attempt, CancellationToken cancellationToken) {
        using var timeout = new CancellationTokenSource(_options.AttemptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = _time.GetTimestamp();
        try {
            using var message = CreateMessage(target, request);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var elapsedMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
            LogRequest(request.Method.Method, request.Template, (int)response.StatusCode, elapsedMs);

            var status = (int)response.StatusCode;
            if (status >= 500) {
                _throttle.RecordFailure(target.Team);
            } else {
                _throttle.RecordSuccess(target.Team);
            }

            if (response.IsSuccessStatusCode) {
                return request.Attachment
                    ? await ReadAttachmentAsync(response, linked.Token)
                    : await ReadBodyAsync(response, linked.Token);
            }
            if (status is >= 300 and < 400) {
                return request.Attachment
                    ? await FollowMediaRedirectAsync(response.Headers.Location, linked.Token)
                    : Outcome.Fail(JiraError.Unavailable);
            }

            var retryAfter = RetryAfter(response);
            var error = await MapErrorAsync(response, retryAfter, linked.Token);
            var retryable = status is 429 or 503 && request.RetryOnResponse;
            return Outcome.Fail(error, retryable ? RetryDelay(retryAfter, attempt) : null);
        } catch (HttpRequestException e) when (Find<BlockedAddressException>(e) is not null) {
            return Outcome.Fail(JiraError.Unavailable, exception: e);
        } catch (HttpRequestException e) when (e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError) {
            _throttle.RecordFailure(target.Team);
            return Outcome.Fail(JiraError.Unavailable, Backoff(attempt), e);
        } catch (HttpRequestException e) {
            return Outcome.Fail(JiraError.Unavailable, exception: e);
        } catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            _throttle.RecordFailure(target.Team);
            return Outcome.Fail(JiraError.Unavailable, exception: e);
        } catch (IOException e) {
            return Outcome.Fail(JiraError.Unavailable, exception: e);
        }
    }

    private HttpRequestMessage CreateMessage(JiraTarget target, JiraRequest request) {
        var message = new HttpRequestMessage(request.Method, BuildUri(target.Host, request.PathAndQuery)) {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicParameter(target));
        if (request.Body is not null) {
            message.Content = new ByteArrayContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return message;
    }

    private string BasicParameter(JiraTarget target) {
        if (target.BasicParameter is not null) {
            return target.BasicParameter;
        }

        return _credentials.TryCreateBasicParameter(target.Team, target.Box)
            ?? throw new JiraException(JiraError.NotConfigured);
    }

    private Uri BuildUri(string host, string pathAndQuery) => new($"{_options.Scheme}://{host}:{_options.Port}{pathAndQuery}");

    private async Task<Outcome> FollowMediaRedirectAsync(Uri? location, CancellationToken cancellationToken) {
        if (!IsAllowedMediaLocation(location)) {
            return Outcome.Fail(JiraError.Unavailable);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(location!.IdnHost, location.PathAndQuery)) {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var started = _time.GetTimestamp();
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var elapsedMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        LogRequest("GET", "media", (int)response.StatusCode, elapsedMs);
        if (response.IsSuccessStatusCode) {
            return await ReadAttachmentAsync(response, cancellationToken);
        }

        return Outcome.Fail((int)response.StatusCode == 404 ? JiraError.NotFound : JiraError.Unavailable);
    }

    /// <summary>Only an absolute https URL on the default port, to the media host or a single-label <c>*.atlassian.net</c> host.</summary>
    public static bool IsAllowedMediaLocation(Uri? location) =>
        location is { IsAbsoluteUri: true }
        && location.Scheme == Uri.UriSchemeHttps
        && location.IsDefaultPort
        && string.IsNullOrEmpty(location.UserInfo)
        && location.HostNameType == UriHostNameType.Dns
        && (location.IdnHost == MediaHost || AtlassianSite().IsMatch(location.IdnHost));

    private async Task<Outcome> ReadAttachmentAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType is null || !AttachmentTypes.Contains(mediaType)) {
            return Outcome.Fail(JiraError.AttachmentUnsupported);
        }

        var body = await ReadCappedAsync(response.Content, _options.MaxAttachmentBytes, cancellationToken);
        return body is null
            ? Outcome.Fail(JiraError.AttachmentTooLarge)
            : Outcome.Ok(new JiraResponse(response.StatusCode, body, mediaType));
    }

    private async Task<Outcome> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        var body = await ReadCappedAsync(response.Content, _options.MaxResponseBytes, cancellationToken);
        return body is null
            ? Outcome.Fail(JiraError.Unavailable)
            : Outcome.Ok(new JiraResponse(response.StatusCode, body, response.Content.Headers.ContentType?.MediaType));
    }

    private static async Task<JiraError> MapErrorAsync(HttpResponseMessage response, TimeSpan? retryAfter, CancellationToken cancellationToken) {
        var body = response.StatusCode == HttpStatusCode.BadRequest
            ? await ReadCappedAsync(response.Content, 64 * 1024, cancellationToken) ?? []
            : [];
        var denied = response.Headers.TryGetValues("X-Seraph-LoginReason", out var reasons)
            && reasons.Any(r => r.Contains("AUTHENTICATION_DENIED", StringComparison.OrdinalIgnoreCase));
        return JiraMap.MapStatus(response.StatusCode, body, denied, retryAfter);
    }

    /// <summary>The body, read through a counter, so a decompressed or chunked body cannot exceed the cap; null when it does.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken) {
        if (content.Headers.ContentLength > maxBytes) {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
                if (buffer.Length + read > maxBytes) {
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
        } finally {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (header?.Date is { } date) {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    // Retry-After wins when Jira sends it; one longer than the cap is not waited out (the caller gets rate_limited
    // with the value instead). Without it, full-jitter exponential backoff.
    private TimeSpan? RetryDelay(TimeSpan? retryAfter, int attempt) =>
        retryAfter is { } wait
            ? wait <= _options.MaxRetryAfter ? wait : null
            : Backoff(attempt);

    private TimeSpan Backoff(int attempt) {
        var ceiling = Math.Min(_options.BackoffCap.TotalMilliseconds, _options.BackoffBase.TotalMilliseconds * (1 << Math.Min(attempt, 8)));
        return TimeSpan.FromMilliseconds(ceiling * _options.Jitter());
    }

    private async Task MarkAuthFailedAsync(TeamId team) {
        try {
            await _credentials.MarkStatusAsync(team, JiraConfigStatus.AuthFailed, CancellationToken.None);
        } catch (NpgsqlException e) {
            LogStatusWriteFailed(e);
        }
    }

    private static SocketsHttpHandler CreateHandler(JiraClientOptions options) => new() {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(options, context.DnsEndPoint, cancellationToken),
    };

    // Resolves the host itself and connects only to a vetted address, so neither DNS rebinding nor a redirect can
    // reach an internal host. Every resolved address must pass: one private answer refuses the host.
    private static async ValueTask<Stream> ConnectAsync(JiraClientOptions options, DnsEndPoint endPoint, CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        IPAddress[] addresses;
        try {
            addresses = IPAddress.TryParse(endPoint.Host, out var literal)
                ? [literal]
                : await options.Resolver.ResolveAsync(endPoint.Host, timeout.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new IOException("DNS resolution timed out.");
        }

        if (addresses.Length == 0 || addresses.Any(a => !options.IsAllowedAddress(a))) {
            throw new BlockedAddressException();
        }

        Exception? last = null;
        foreach (var address in addresses) {
            var target = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try {
                await socket.ConnectAsync(new IPEndPoint(target, endPoint.Port), timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            } catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested) {
                socket.Dispose();
                last = e;
            }
        }

        throw new IOException("Could not connect to Jira.", last);
    }

    private static T Deserialize<T>(JiraResponse response, JsonTypeInfo<T> typeInfo) {
        try {
            return JsonSerializer.Deserialize(response.Body, typeInfo) ?? throw new JiraException(JiraError.Unavailable);
        } catch (JsonException e) {
            throw new JiraException(JiraError.Unavailable, e);
        }
    }

    private static T? Find<T>(Exception exception) where T : Exception {
        for (var current = exception; current is not null; current = current.InnerException) {
            if (current is T match) {
                return match;
            }
        }
        return null;
    }

    private static string BodyHash(byte[]? body) =>
        body is null ? "" : Convert.ToHexString(SHA256.HashData(body));

    private static void KeepTail(List<JiraSprint> ring, IReadOnlyList<JiraSprint> values, int keep) {
        ring.AddRange(values);
        if (ring.Count > keep) {
            ring.RemoveRange(0, ring.Count - keep);
        }
    }

    private static byte[] MoveBody(IReadOnlyList<string> issueKeys) {
        ArgumentNullException.ThrowIfNull(issueKeys);
        if (issueKeys.Count is 0 or > 50) {
            throw new ArgumentException("Between 1 and 50 issues move at once.", nameof(issueKeys));
        }
        return JsonSerializer.SerializeToUtf8Bytes(
            new JiraMoveIssuesRequest([.. issueKeys.Select(RequireKey)]), JiraClientJsonContext.Default.JiraMoveIssuesRequest);
    }

    private static string IssuePageQuery(string? jql, IReadOnlyList<string> fields, int startAt) {
        var query = new StringBuilder();
        query.Append(CultureInfo.InvariantCulture, $"startAt={startAt}&maxResults=100&fields={Query(string.Join(',', fields))}");
        if (!string.IsNullOrEmpty(jql)) {
            query.Append("&jql=").Append(Query(jql));
        }
        return query.ToString();
    }

    private static byte[] WriteJson(Action<Utf8JsonWriter> write) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteIdRef(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string id) {
        writer.WriteStartObject(name);
        writer.WriteString("id"u8, id);
        writer.WriteEndObject();
    }

    private static void WriteAccount(Utf8JsonWriter writer, string? accountId) {
        if (accountId is null) {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("accountId"u8, accountId);
        writer.WriteEndObject();
    }

    private static JiraRequest Get(string pathAndQuery, string template) =>
        new(HttpMethod.Get, pathAndQuery, template, null, RetryOnResponse: true, Shared: true);

    private static JiraRequest Post(string pathAndQuery, string template, byte[] body) =>
        new(HttpMethod.Post, pathAndQuery, template, body, RetryOnResponse: false, Shared: false);

    private static string RequireKey(string issueKey) =>
        JiraMap.IsIssueKey(issueKey) ? issueKey : throw new ArgumentException("Not an issue key.", nameof(issueKey));

    private static string RequireProjectKey(string projectKey) =>
        projectKey is not null && ProjectKey().IsMatch(projectKey) ? projectKey : throw new ArgumentException("Not a project key.", nameof(projectKey));

    private static void RequireNumeric(string value, string name) {
        if (value is null || !NumericId().IsMatch(value)) {
            throw new ArgumentException("Jira ids are numeric.", name);
        }
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static string Query(string value) => Uri.EscapeDataString(value);

    [GeneratedRegex(@"^[0-9]{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericId();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectKey();

    [GeneratedRegex(@"^[a-z0-9-]+\.atlassian\.net\z", RegexOptions.CultureInvariant)]
    private static partial Regex AtlassianSite();

    [LoggerMessage(Level = LogLevel.Information, Message = "Jira {Method} {Template} {Status} {ElapsedMs} ms")]
    private partial void LogRequest(string method, string template, int status, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record a Jira authentication failure")]
    private partial void LogStatusWriteFailed(Exception exception);

    private sealed record JiraTarget(TeamId Team, string Host, byte[]? Box, string? BasicParameter, bool Saved);

    private sealed record JiraRequest(
        HttpMethod Method,
        string PathAndQuery,
        string Template,
        byte[]? Body,
        bool RetryOnResponse,
        bool Shared,
        bool Attachment = false);

    private sealed record JiraResponse(HttpStatusCode Status, byte[] Body, string? ContentType);

    private sealed record Outcome(JiraResponse? Response, JiraError? Error, TimeSpan? RetryIn, Exception? Exception) {
        public static Outcome Ok(JiraResponse response) => new(response, null, null, null);

        public static Outcome Fail(JiraError error, TimeSpan? retryIn = null, Exception? exception = null) =>
            new(null, error, retryIn, exception);
    }

    private sealed class BlockedAddressException() : IOException("The Jira host resolves to an address that is not allowed.");
}
