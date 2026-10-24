using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Retroscope.Fakes.FakeJira;

/// <summary>One request the fake received. <see cref="Host"/> is the Host header without the port.</summary>
public sealed record FakeJiraRequest(string Method, string Host, string Path, string Query, string? Authorization, string Body);

/// <summary>A scripted reply that replaces the fake's normal behaviour for one matching request.</summary>
public sealed record FakeJiraResponse(
    int Status,
    string? Json = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Bytes = null,
    string? ContentType = null,
    TimeSpan Delay = default);

/// <summary>
/// An in-process Jira Cloud stand-in on plain HTTP over loopback: the REST v3 and Agile 1.0 endpoints Retroscope uses
/// (DESIGN §f), Basic auth, the attachment redirect to the media host, and scripted replies for failure tests. Every
/// host name is served, so one instance plays the team's site and <c>api.media.atlassian.com</c> at once.
/// </summary>
public sealed partial class FakeJiraServer : IAsyncDisposable {
    public const string MediaHost = "api.media.atlassian.com";

    private const string RawPlaceholder = "fake-jira-raw-description";

    private static readonly JsonObject[] LinkTypes = [
        new JsonObject { ["id"] = "10000", ["name"] = "Blocks", ["inward"] = "is blocked by", ["outward"] = "blocks" },
    ];

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<FakeJiraRequest> _requests = new();
    private readonly Lock _scriptGate = new();
    private readonly List<(string Method, string PathPrefix, Queue<FakeJiraResponse> Replies)> _scripts = [];
    private readonly ConcurrentDictionary<string, long> _ranks = new(StringComparer.Ordinal);
    private int _nextId = 20000;
    private int _nextLinkId = 10000;
    private long _nextRank;

    private FakeJiraServer(WebApplication app) {
        _app = app;
    }

    public int Port { get; private set; }

    public string Email { get; set; } = "bot@acme.test";

    public string Token { get; set; } = "ATATT-fake-token-1234";

    /// <summary>Every request waits for this before it is answered, when set (to hold requests in flight).</summary>
    public Task? Gate { get; set; }

    /// <summary>Answer attachment downloads with a 303 to the media host even when <c>redirect=false</c> was asked for.</summary>
    public bool RedirectAttachments { get; set; }

    /// <summary>Agile pages leave out <c>total</c>, as some sites do.</summary>
    public bool OmitTotals { get; set; }

    /// <summary>Overrides the Location of the attachment redirect.</summary>
    public string? AttachmentLocation { get; set; }

    public JsonArray Fields { get; } = DefaultFields();

    public ConcurrentDictionary<string, JsonObject> Issues { get; } = new(StringComparer.Ordinal);

    public List<JsonObject> Boards { get; } = [];

    public List<JsonObject> Sprints { get; } = [];

    public ConcurrentDictionary<string, (string ContentType, byte[] Bytes)> Attachments { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Descriptions served exactly as written (formatting and escapes included), by issue key, in place of the issue's
    /// parsed description: what a byte-for-byte check compares against. A PUT of the description replaces it.
    /// </summary>
    public ConcurrentDictionary<string, string> RawDescriptions { get; } = new(StringComparer.Ordinal);

    public string PointsFieldId { get; set; } = "customfield_10016";

    public string SprintFieldId { get; set; } = "customfield_10020";

    public IReadOnlyList<FakeJiraRequest> Requests => [.. _requests];

    public static async Task<FakeJiraServer> StartAsync() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeJiraServer(app);
        app.Run(server.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Port = new Uri(address).Port;
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public string BasicAuthorization => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Token}"));

    /// <summary>The next requests whose method matches and whose path starts with <paramref name="pathPrefix"/> get these replies, in order.</summary>
    public void Script(string method, string pathPrefix, params FakeJiraResponse[] replies) {
        lock (_scriptGate) {
            _scripts.Add((method, pathPrefix, new Queue<FakeJiraResponse>(replies)));
        }
    }

    public void ClearRequests() => _requests.Clear();

    /// <summary>
    /// Adds an issue ranked after every issue so far. <paramref name="status"/> "Done" is in the Done category, "In
    /// Progress" and "In Review" in In Progress, anything else in To Do.
    /// </summary>
    public JsonObject AddIssue(
        string key,
        string summary,
        long? sprintId = null,
        decimal? points = null,
        string status = "To Do",
        string issueType = "Story") {
        var (categoryKey, categoryName) = status switch {
            "Done" => ("done", "Done"),
            "In Progress" or "In Review" => ("indeterminate", "In Progress"),
            _ => ("new", "To Do"),
        };
        var fields = new JsonObject {
            ["summary"] = summary,
            ["status"] = new JsonObject {
                ["id"] = "1",
                ["name"] = status,
                ["statusCategory"] = new JsonObject { ["key"] = categoryKey, ["name"] = categoryName },
            },
            ["issuetype"] = new JsonObject {
                ["id"] = issueType == "Story" ? "10001" : NextId(),
                ["name"] = issueType,
                ["subtask"] = issueType == "Sub-task",
            },
            ["priority"] = new JsonObject { ["id"] = "3", ["name"] = "Medium" },
            ["project"] = new JsonObject { ["id"] = "10000", ["key"] = key[..key.IndexOf('-', StringComparison.Ordinal)], ["name"] = "Project" },
            ["created"] = "2026-01-05T10:00:00.000+0000",
            ["updated"] = "2026-01-06T10:00:00.000+0000",
            ["attachment"] = new JsonArray(),
            ["labels"] = new JsonArray(),
        };
        if (points is { } value) {
            fields[PointsFieldId] = value;
        }
        if (sprintId is { } id && Sprints.FirstOrDefault(s => (long)s["id"]! == id) is { } sprint) {
            fields[SprintFieldId] = new JsonArray(sprint.DeepClone());
        }

        var issue = new JsonObject { ["id"] = NextId(), ["key"] = key, ["fields"] = fields };
        Issues[key] = issue;
        _ranks[key] = Interlocked.Increment(ref _nextRank);
        return issue;
    }

    /// <summary>Moves <paramref name="key"/> to the top of the rank order, as dragging it in Jira would.</summary>
    public void RankFirst(string key) => _ranks[key] = _ranks.Values.DefaultIfEmpty(0).Min() - 1;

    /// <summary>The ids of every issue link, as Jira holds them.</summary>
    public IReadOnlyList<string> LinkIds => [.. Issues.Values
        .SelectMany(issue => issue["fields"]!["issuelinks"] as JsonArray ?? [])
        .Select(link => (string)link!["id"]!)
        .Distinct()];

    /// <param name="originBoardId">The board that owns the sprint; a board's sprint list leaves out other boards' sprints.</param>
    public JsonObject AddSprint(
        long id,
        string name,
        string state,
        string? startDate,
        string? endDate = null,
        string? completeDate = null,
        string? goal = null,
        long? originBoardId = null) {
        var sprint = new JsonObject {
            ["id"] = id,
            ["name"] = name,
            ["state"] = state,
            ["startDate"] = startDate,
            ["endDate"] = endDate,
            ["completeDate"] = completeDate,
            ["goal"] = goal,
            ["originBoardId"] = originBoardId,
        };
        foreach (var unset in sprint.Where(member => member.Value is null).Select(member => member.Key).ToList()) {
            sprint.Remove(unset);
        }
        Sprints.Add(sprint);
        return sprint;
    }

    /// <summary>Replaces the issue's whole sprint field, in order: how an issue carried from sprint to sprint looks.</summary>
    public void SetSprints(string key, params long[] sprintIds) =>
        Issues[key]["fields"]![SprintFieldId] = sprintIds.Length == 0
            ? null
            : new JsonArray([.. sprintIds.Select(id => Sprints.First(s => (long)s["id"]! == id).DeepClone())]);

    /// <summary>Sets the issue's resolution date; null clears it.</summary>
    public void Resolve(string key, string? resolutiondate) => Issues[key]["fields"]!["resolutiondate"] = resolutiondate;

    public JsonObject AddBoard(long id, string projectKey, string type = "scrum") {
        var board = new JsonObject {
            ["id"] = id,
            ["name"] = $"{projectKey} board",
            ["type"] = type,
            ["location"] = new JsonObject { ["projectKey"] = projectKey, ["projectId"] = 10000 },
        };
        Boards.Add(board);
        return board;
    }

    public void AddAttachment(string issueKey, string id, string filename, string contentType, byte[] bytes) {
        Attachments[id] = (contentType, bytes);
        var list = (JsonArray)Issues[issueKey]["fields"]!["attachment"]!;
        list.Add(new JsonObject { ["id"] = id, ["filename"] = filename, ["mimeType"] = contentType, ["size"] = bytes.Length });
    }

    private static JsonArray DefaultFields() => [
        Field("summary", "Summary", custom: null),
        Field("customfield_10016", "Story point estimate", "com.pyxis.greenhopper.jira:jsw-story-points"),
        Field("customfield_10020", "Sprint", "com.pyxis.greenhopper.jira:gh-sprint"),
        Field("customfield_10028", "Story Points", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
    ];

    public static JsonObject Field(string id, string name, string? custom) {
        var field = new JsonObject { ["id"] = id, ["name"] = name, ["custom"] = custom is not null };
        if (custom is not null) {
            field["schema"] = new JsonObject { ["type"] = "number", ["custom"] = custom };
        }
        return field;
    }

    private string NextId() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);

    private async Task HandleAsync(HttpContext context) {
        var request = context.Request;
        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
            body = await reader.ReadToEndAsync();
        }

        var host = request.Host.Host;
        var authorization = request.Headers.Authorization.Count > 0 ? request.Headers.Authorization.ToString() : null;
        _requests.Enqueue(new FakeJiraRequest(request.Method, host, request.Path.Value ?? "", request.QueryString.Value ?? "", authorization, body));

        if (Gate is { } gate) {
            await gate;
        }

        if (TakeScript(request.Method, request.Path.Value ?? "") is { } scripted) {
            await WriteScriptedAsync(context, scripted);
            return;
        }

        if (host == MediaHost) {
            await ServeMediaAsync(context);
            return;
        }

        if (authorization != BasicAuthorization) {
            await WriteJsonAsync(context, 401, new JsonObject { ["errorMessages"] = new JsonArray("Client must be authenticated.") });
            return;
        }

        var (status, json) = Route(request.Method, request.Path.Value ?? "", request.Query, body);
        if (status == 0) {
            await ServeAttachmentAsync(context);
            return;
        }
        if (request.Method == "GET" && status == 200 && IssuePath().Match(request.Path.Value ?? "") is { Success: true } issuePath
            && RawDescriptions.TryGetValue(issuePath.Groups[1].Value, out var raw)) {
            json!["fields"]!["description"] = RawPlaceholder;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(json.ToJsonString().Replace($"\"{RawPlaceholder}\"", raw, StringComparison.Ordinal), context.RequestAborted);
            return;
        }

        await WriteJsonAsync(context, status, json);
    }

    private FakeJiraResponse? TakeScript(string method, string path) {
        lock (_scriptGate) {
            foreach (var (scriptMethod, prefix, replies) in _scripts) {
                if (scriptMethod == method && path.StartsWith(prefix, StringComparison.Ordinal) && replies.Count > 0) {
                    return replies.Dequeue();
                }
            }
        }
        return null;
    }

    private static async Task WriteScriptedAsync(HttpContext context, FakeJiraResponse reply) {
        if (reply.Delay > TimeSpan.Zero) {
            await Task.Delay(reply.Delay, context.RequestAborted);
        }

        context.Response.StatusCode = reply.Status;
        foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>()) {
            context.Response.Headers[name] = value;
        }

        if (reply.Bytes is not null) {
            context.Response.ContentType = reply.ContentType ?? "application/octet-stream";
            await context.Response.Body.WriteAsync(reply.Bytes, context.RequestAborted);
        } else if (reply.Json is not null) {
            context.Response.ContentType = reply.ContentType ?? "application/json";
            await context.Response.WriteAsync(reply.Json, context.RequestAborted);
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode? json) {
        context.Response.StatusCode = status;
        if (json is not null) {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(json.ToJsonString(), context.RequestAborted);
        }
    }

    private async Task ServeAttachmentAsync(HttpContext context) {
        var id = context.Request.Path.Value!["/rest/api/3/attachment/content/".Length..];
        if (!Attachments.TryGetValue(id, out var attachment)) {
            await WriteJsonAsync(context, 404, new JsonObject { ["errorMessages"] = new JsonArray("Attachment not found.") });
            return;
        }

        if (RedirectAttachments || context.Request.Query["redirect"] != "false") {
            context.Response.StatusCode = 303;
            context.Response.Headers.Location = AttachmentLocation ?? $"https://{MediaHost}/file/{id}/binary?token=signed&client=fake";
            return;
        }

        context.Response.ContentType = attachment.ContentType;
        await context.Response.Body.WriteAsync(attachment.Bytes, context.RequestAborted);
    }

    private async Task ServeMediaAsync(HttpContext context) {
        var match = MediaPath().Match(context.Request.Path.Value ?? "");
        if (!match.Success || context.Request.Query["token"] != "signed" || !Attachments.TryGetValue(match.Groups[1].Value, out var attachment)) {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = attachment.ContentType;
        await context.Response.Body.WriteAsync(attachment.Bytes, context.RequestAborted);
    }

    // Status 0 means "serve an attachment".
    private (int Status, JsonNode? Json) Route(string method, string path, IQueryCollection query, string body) {
        Match m;
        switch (method) {
            case "GET" when path == "/rest/api/3/myself":
                return (200, new JsonObject { ["accountId"] = "acc-bot", ["displayName"] = "Retroscope Bot", ["emailAddress"] = Email });
            case "GET" when path == "/rest/api/3/field":
                return (200, Fields.DeepClone());
            case "GET" when path == "/rest/agile/1.0/board":
                return (200, ValuesPage(Boards.Where(b => (string?)b["location"]!["projectKey"] == query["projectKeyOrId"]).ToList(), query));
            case "GET" when (m = BoardPath().Match(path)).Success:
                return Boards.FirstOrDefault(b => (long)b["id"]! == long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) is { } board
                    ? (200, board.DeepClone())
                    : NotFound("Board does not exist.");
            case "GET" when (m = BoardSprintsPath().Match(path)).Success: {
                var states = ((string?)query["state"] ?? "active,future,closed").Split(',');
                var boardId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return (200, ValuesPage(Sprints.Where(s => states.Contains((string?)s["state"]) && ((long?)s["originBoardId"] ?? boardId) == boardId).ToList(), query));
            }
            case "GET" when (m = SprintIssuesPath().Match(path)).Success: {
                var sprintId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return AgilePage(i => InSprint(i, sprintId), query);
            }
            case "GET" when (m = BacklogPath().Match(path)).Success:
                return AgilePage(i => i["fields"]![SprintFieldId] is null, query);
            case "POST" when (m = MoveToSprintPath().Match(path)).Success:
                return MoveIssues(body, Sprints.FirstOrDefault(s => (long)s["id"]! == long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
            case "POST" when BacklogMovePath().IsMatch(path):
                return MoveIssues(body, null);
            case "POST" when path == "/rest/api/3/search/jql":
                return Search(body);
            case "GET" when (m = IssuePath().Match(path)).Success:
                return Issues.TryGetValue(m.Groups[1].Value, out var issue) ? (200, issue.DeepClone()) : NotFound("Issue does not exist or you do not have permission to see it.");
            case "PUT" when (m = IssuePath().Match(path)).Success:
                return UpdateIssue(m.Groups[1].Value, body);
            case "GET" when (m = IssueChildPath().Match(path)).Success && m.Groups[2].Value == "comment":
                return WithIssue(m.Groups[1].Value, i => (200, new JsonObject {
                    ["startAt"] = 0, ["maxResults"] = 20, ["total"] = 0, ["comments"] = new JsonArray(),
                }));
            case "POST" when (m = IssueChildPath().Match(path)).Success && m.Groups[2].Value == "comment":
                return WithIssue(m.Groups[1].Value, _ => (201, new JsonObject { ["id"] = NextId() }));
            case "GET" when (m = IssueChildPath().Match(path)).Success && m.Groups[2].Value == "editmeta":
                return WithIssue(m.Groups[1].Value, _ => (200, JsonNode.Parse("""
                    {"fields":{"priority":{"allowedValues":[{"id":"1","name":"Highest"},{"id":"3","name":"Medium"}]},
                    "issuetype":{"allowedValues":[{"id":"10001","name":"Story","subtask":false},{"id":"10002","name":"Bug","subtask":false}]}}}
                    """)));
            case "GET" when (m = IssueChildPath().Match(path)).Success && m.Groups[2].Value == "transitions":
                return WithIssue(m.Groups[1].Value, _ => (200, JsonNode.Parse("""
                    {"transitions":[{"id":"21","name":"Start","to":{"id":"3","name":"In Progress","statusCategory":{"key":"indeterminate"}}},
                    {"id":"31","name":"Done","to":{"id":"4","name":"Done","statusCategory":{"key":"done"}}}]}
                    """)));
            case "POST" when (m = IssueChildPath().Match(path)).Success && m.Groups[2].Value == "transitions":
                return WithIssue(m.Groups[1].Value, _ => (204, null));
            case "GET" when (m = ProjectPath().Match(path)).Success:
                return (200, new JsonObject {
                    ["id"] = "10000", ["key"] = m.Groups[1].Value, ["name"] = "Project",
                    ["issueTypes"] = JsonNode.Parse("""[{"id":"10001","name":"Story","subtask":false},{"id":"10003","name":"Sub-task","subtask":true}]"""),
                });
            case "GET" when (m = CreateMetaPath().Match(path)).Success:
                return (200, JsonNode.Parse("""
                    {"startAt":0,"maxResults":200,"total":2,"issueTypes":[{"id":"10001","name":"Story","subtask":false},{"id":"10003","name":"Sub-task","subtask":true}]}
                    """));
            case "GET" when path == "/rest/api/3/issueLinkType":
                return (200, new JsonObject { ["issueLinkTypes"] = new JsonArray([.. LinkTypes.Select(t => t.DeepClone())]) });
            case "POST" when path == "/rest/api/3/issueLink":
                return CreateLink(body);
            case "DELETE" when (m = IssueLinkPath().Match(path)).Success:
                return DeleteLink(m.Groups[1].Value);
            case "GET" when path == "/rest/api/3/user/assignable/search":
                return (200, JsonNode.Parse("""[{"accountId":"acc-1","displayName":"Ada Lovelace"}]"""));
            case "POST" when path == "/rest/api/3/issue":
                return CreateIssue(body);
            case "GET" when path.StartsWith("/rest/api/3/attachment/content/", StringComparison.Ordinal):
                return (0, null);
            default:
                return NotFound("No such endpoint in FakeJira.");
        }
    }

    private (int, JsonNode?) WithIssue(string key, Func<JsonObject, (int, JsonNode?)> then) =>
        Issues.TryGetValue(key, out var issue) ? then(issue) : NotFound("Issue does not exist or you do not have permission to see it.");

    private static (int, JsonNode?) NotFound(string message) =>
        (404, new JsonObject { ["errorMessages"] = new JsonArray(message), ["errors"] = new JsonObject() });

    private bool InSprint(JsonObject issue, long sprintId) =>
        issue["fields"]![SprintFieldId] is JsonArray sprints && sprints.Any(s => (long)s!["id"]! == sprintId);

    private (int, JsonNode?) UpdateIssue(string key, string body) {
        if (!Issues.TryGetValue(key, out var issue)) {
            return NotFound("Issue does not exist or you do not have permission to see it.");
        }

        var update = JsonNode.Parse(body)!["fields"]!.AsObject();
        var errors = new JsonObject();
        foreach (var (name, _) in update) {
            if (name != "summary" && name != "description" && name != "assignee" && name != "priority"
                && name != "issuetype" && name != PointsFieldId) {
                errors[name] = $"Field '{name}' cannot be set. It is not on the appropriate screen, or unknown.";
            }
        }
        if (errors.Count > 0) {
            return (400, new JsonObject { ["errorMessages"] = new JsonArray(), ["errors"] = errors });
        }

        if (update["description"] is JsonNode description && ReservedNode(description) is { } reserved) {
            // Jira's REST validator refuses unknown node types; the fake is never more lenient than Jira.
            return (400, new JsonObject {
                ["errorMessages"] = new JsonArray(),
                ["errors"] = new JsonObject { ["description"] = $"INVALID_INPUT: unknown node or attribute {reserved}" },
            });
        }

        var fields = issue["fields"]!.AsObject();
        foreach (var (name, value) in update) {
            fields[name] = value?.DeepClone();
        }
        if (update.ContainsKey("description")) {
            RawDescriptions.TryRemove(key, out _);
        }
        fields["updated"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+0000'", CultureInfo.InvariantCulture);
        return (204, null);
    }

    // The server's own token names (rsOpaque, rsMark, rsKeep…) must never reach Jira.
    private static string? ReservedNode(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var (name, value) in obj) {
                    if (IsReserved(name) || name == "type" && value is JsonValue type && type.TryGetValue<string>(out var text) && IsReserved(text)) {
                        return name == "type" ? (string)value! : name;
                    }
                    if (ReservedNode(value) is { } found) {
                        return found;
                    }
                }
                return null;
            case JsonArray array:
                return array.Select(ReservedNode).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    private static bool IsReserved(string name) => name.Length > 2 && name.StartsWith("rs", StringComparison.Ordinal) && char.IsAsciiLetterUpper(name[2]);

    private (int, JsonNode?) MoveIssues(string body, JsonObject? sprint) {
        foreach (var key in JsonNode.Parse(body)!["issues"]!.AsArray()) {
            if (!Issues.TryGetValue((string)key!, out var issue)) {
                return NotFound("Issue does not exist.");
            }
            issue["fields"]![SprintFieldId] = sprint is null ? null : new JsonArray(sprint.DeepClone());
        }
        return (204, null);
    }

    private (int, JsonNode?) CreateIssue(string body) {
        var fields = JsonNode.Parse(body)!["fields"]!.AsObject();
        if (fields["summary"] is null) {
            return (400, new JsonObject { ["errors"] = new JsonObject { ["summary"] = "You must specify a summary of the issue." } });
        }

        var projectKey = (string)fields["project"]!["key"]!;
        var key = $"{projectKey}-{Issues.Count + 1}";
        var issue = AddIssue(key, (string)fields["summary"]!);
        if (fields["description"] is { } description) {
            issue["fields"]!["description"] = description.DeepClone();
        }
        return (201, new JsonObject { ["id"] = issue["id"]!.DeepClone(), ["key"] = key, ["self"] = $"https://fake/rest/api/3/issue/{key}" });
    }

    private (int, JsonNode?) Search(string body) {
        var request = JsonNode.Parse(body)!;
        var maxResults = (int?)request["maxResults"] ?? 50;
        var start = int.TryParse((string?)request["nextPageToken"], CultureInfo.InvariantCulture, out var token) ? token : 0;
        if (Query((string?)request["jql"] ?? "") is not { } query) {
            return JqlError((string?)request["jql"] ?? "");
        }

        var page = Select(query, query.ByRank).Skip(start).Take(maxResults + 1).ToList();
        var isLast = page.Count <= maxResults;
        var result = new JsonObject {
            ["issues"] = new JsonArray([.. page.Take(maxResults).Select(i => i.DeepClone())]),
            ["isLast"] = isLast,
        };
        if (!isLast) {
            result["nextPageToken"] = (start + maxResults).ToString(CultureInfo.InvariantCulture);
        }
        return (200, result);
    }

    // The Agile issue endpoints filter by their optional jql parameter and answer in rank order, as Jira does.
    private (int, JsonNode?) AgilePage(Func<JsonObject, bool> scope, IQueryCollection query) {
        var jql = (string?)query["jql"] ?? "";
        if (Query(jql) is not { } filter) {
            return JqlError(jql);
        }
        var all = Select(filter, byRank: true).Where(scope).ToList();
        var startAt = int.TryParse(query["startAt"], CultureInfo.InvariantCulture, out var s) ? s : 0;
        var maxResults = int.TryParse(query["maxResults"], CultureInfo.InvariantCulture, out var m) ? m : 50;
        return (200, new JsonObject {
            ["startAt"] = startAt,
            ["maxResults"] = maxResults,
            ["total"] = all.Count,
            ["issues"] = new JsonArray([.. all.Skip(startAt).Take(maxResults).Select(i => i.DeepClone())]),
        });
    }

    private FakeJqlQuery? Query(string jql) {
        try {
            return FakeJql.Parse(jql, SprintFieldId, id => (string?)Sprints.FirstOrDefault(s => (long)s["id"]! == id)?["state"]);
        } catch (FakeJqlException) {
            return null;
        }
    }

    private static (int, JsonNode?) JqlError(string jql) =>
        (400, new JsonObject { ["errorMessages"] = new JsonArray($"Error in the JQL Query: {jql}"), ["errors"] = new JsonObject() });

    private IEnumerable<JsonObject> Select(FakeJqlQuery query, bool byRank) {
        var matching = Issues.Values.Where(query.Matches);
        return byRank
            ? matching.OrderBy(i => _ranks.GetValueOrDefault((string)i["key"]!))
            : matching.OrderBy(i => (string)i["key"]!, StringComparer.Ordinal);
    }

    // Each issue lists the link with the other issue on the side the request named for it.
    private (int, JsonNode?) CreateLink(string body) {
        var request = JsonNode.Parse(body)!;
        var name = (string?)request["type"]?["name"];
        if (!Issues.TryGetValue((string?)request["inwardIssue"]?["key"] ?? "", out var inward)
            || !Issues.TryGetValue((string?)request["outwardIssue"]?["key"] ?? "", out var outward)) {
            return NotFound("Issue does not exist or you do not have permission to see it.");
        }
        if (LinkTypes.FirstOrDefault(t => (string?)t["name"] == name) is not { } type) {
            return (400, new JsonObject { ["errorMessages"] = new JsonArray($"No issue link type with name '{name}' found."), ["errors"] = new JsonObject() });
        }
        var id = Interlocked.Increment(ref _nextLinkId).ToString(CultureInfo.InvariantCulture);
        AddLink(outward, id, type, "outwardIssue", inward);
        AddLink(inward, id, type, "inwardIssue", outward);
        return (201, null);
    }

    private static void AddLink(JsonObject issue, string id, JsonObject type, string side, JsonObject other) {
        var fields = issue["fields"]!.AsObject();
        if (fields["issuelinks"] is not JsonArray links) {
            fields["issuelinks"] = links = [];
        }
        links.Add(new JsonObject {
            ["id"] = id,
            ["type"] = type.DeepClone(),
            [side] = new JsonObject {
                ["key"] = other["key"]!.DeepClone(),
                ["fields"] = new JsonObject {
                    ["summary"] = other["fields"]!["summary"]!.DeepClone(),
                    ["status"] = other["fields"]!["status"]!.DeepClone(),
                },
            },
        });
    }

    private (int, JsonNode?) DeleteLink(string id) {
        var removed = 0;
        foreach (var issue in Issues.Values) {
            if (issue["fields"]!["issuelinks"] is JsonArray links) {
                removed += links.RemoveAll(link => (string?)link!["id"] == id);
            }
        }
        return removed > 0 ? (204, null) : NotFound("No issue link with id " + id + " exists.");
    }

    private JsonObject ValuesPage(List<JsonObject> all, IQueryCollection query) {
        var startAt = int.TryParse(query["startAt"], CultureInfo.InvariantCulture, out var s) ? s : 0;
        var maxResults = int.TryParse(query["maxResults"], CultureInfo.InvariantCulture, out var m) ? m : 50;
        var page = all.Skip(startAt).Take(maxResults).ToList();
        var result = new JsonObject {
            ["startAt"] = startAt,
            ["maxResults"] = maxResults,
            ["isLast"] = startAt + page.Count >= all.Count,
            ["values"] = new JsonArray([.. page.Select(v => v.DeepClone())]),
        };
        if (!OmitTotals) {
            result["total"] = all.Count;
        }
        return result;
    }

    [GeneratedRegex(@"^/rest/agile/1\.0/board/([0-9]+)$")]
    private static partial Regex BoardPath();

    [GeneratedRegex(@"^/rest/agile/1\.0/board/([0-9]+)/sprint$")]
    private static partial Regex BoardSprintsPath();

    [GeneratedRegex(@"^/rest/agile/1\.0/sprint/([0-9]+)/issue$")]
    private static partial Regex SprintIssuesPath();

    [GeneratedRegex(@"^/rest/agile/1\.0/board/([0-9]+)/backlog$")]
    private static partial Regex BacklogPath();

    [GeneratedRegex(@"^/rest/agile/1\.0/sprint/([0-9]+)/issue$")]
    private static partial Regex MoveToSprintPath();

    [GeneratedRegex(@"^/rest/agile/1\.0/backlog/([0-9]+)/issue$")]
    private static partial Regex BacklogMovePath();

    [GeneratedRegex(@"^/rest/api/3/issue/([A-Z][A-Z0-9_]*-[0-9]+)$")]
    private static partial Regex IssuePath();

    [GeneratedRegex(@"^/rest/api/3/issue/([A-Z][A-Z0-9_]*-[0-9]+)/(comment|editmeta|transitions)$")]
    private static partial Regex IssueChildPath();

    [GeneratedRegex(@"^/rest/api/3/project/([A-Z][A-Z0-9_]*)$")]
    private static partial Regex ProjectPath();

    [GeneratedRegex(@"^/rest/api/3/issue/createmeta/([A-Z][A-Z0-9_]*)/issuetypes$")]
    private static partial Regex CreateMetaPath();

    [GeneratedRegex(@"^/file/([0-9]+)/binary$")]
    private static partial Regex MediaPath();

    [GeneratedRegex(@"^/rest/api/3/issueLink/([0-9]+)$")]
    private static partial Regex IssueLinkPath();
}
