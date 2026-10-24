using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Retroscope.Core.Sprints;
using Retroscope.Core.Text;
using Retroscope.Server.Json;

namespace Retroscope.Server.Jira;

/// <summary>The stable problem codes of DESIGN §f; endpoints turn a <see cref="JiraError"/> into problem+json.</summary>
public static class JiraErrorCode {
    public const string AuthFailed = "jira_auth_failed";
    public const string Forbidden = "jira_forbidden";
    public const string NotFound = "jira_not_found";
    public const string Validation = "jira_validation";
    public const string RateLimited = "jira_rate_limited";
    public const string Unavailable = "jira_unavailable";
    public const string NotConfigured = "jira_not_configured";
    public const string AttachmentUnsupported = "jira_attachment_unsupported";
    public const string AttachmentTooLarge = "jira_attachment_too_large";
}

/// <param name="Fields">Jira's per-field messages for <see cref="JiraErrorCode.Validation"/>: plain text, at most 300 characters.</param>
/// <param name="RetryAfter">How long Jira asked us to wait, for <see cref="JiraErrorCode.RateLimited"/>.</param>
public sealed record JiraError(
    string Code,
    int HttpStatus,
    IReadOnlyDictionary<string, string>? Fields = null,
    TimeSpan? RetryAfter = null) {
    public static JiraError AuthFailed { get; } = new(JiraErrorCode.AuthFailed, StatusCodes.Status502BadGateway);

    public static JiraError Forbidden { get; } = new(JiraErrorCode.Forbidden, StatusCodes.Status403Forbidden);

    public static JiraError NotFound { get; } = new(JiraErrorCode.NotFound, StatusCodes.Status404NotFound);

    public static JiraError Unavailable { get; } = new(JiraErrorCode.Unavailable, StatusCodes.Status503ServiceUnavailable);

    public static JiraError NotConfigured { get; } = new(JiraErrorCode.NotConfigured, StatusCodes.Status409Conflict);

    public static JiraError AttachmentUnsupported { get; } =
        new(JiraErrorCode.AttachmentUnsupported, StatusCodes.Status415UnsupportedMediaType);

    public static JiraError AttachmentTooLarge { get; } =
        new(JiraErrorCode.AttachmentTooLarge, StatusCodes.Status413PayloadTooLarge);

    public static JiraError RateLimited(TimeSpan? retryAfter) =>
        new(JiraErrorCode.RateLimited, StatusCodes.Status503ServiceUnavailable, RetryAfter: retryAfter);

    public static JiraError Validation(IReadOnlyDictionary<string, string> fields) =>
        new(JiraErrorCode.Validation, StatusCodes.Status422UnprocessableEntity, fields);
}

/// <summary>A Jira call that failed; the message is the code only, so nothing from Jira reaches a log line.</summary>
public sealed class JiraException(JiraError error, Exception? inner = null) : Exception(error.Code, inner) {
    public JiraError Error { get; } = error;
}

/// <param name="PointsFieldId">Never guessed: a site without a discoverable story points field has none.</param>
public sealed record JiraFieldIds(string? PointsFieldId, string? SprintFieldId);

/// <summary>A sprint as read from an issue's sprint field, completed from the board's sprint list when Jira sent only an id.</summary>
public sealed record JiraSprintRef(string Name, string? StartDate, long? Id);

/// <summary>Pure mapping between Jira's documents and ours (DESIGN §f).</summary>
public static partial class JiraMap {
    public const int FieldMessageMaxLength = 300;

    /// <summary>Maps a Jira error response. <paramref name="authenticationDenied"/> is a 403 carrying
    /// <c>X-Seraph-LoginReason: AUTHENTICATION_DENIED</c> (CAPTCHA), which is an auth failure, not a resource denial.</summary>
    public static JiraError MapStatus(HttpStatusCode status, ReadOnlySpan<byte> body, bool authenticationDenied, TimeSpan? retryAfter) {
        var code = (int)status;
        return code switch {
            401 => JiraError.AuthFailed,
            403 when authenticationDenied => JiraError.AuthFailed,
            403 => JiraError.Forbidden,
            404 => JiraError.NotFound,
            429 => JiraError.RateLimited(retryAfter),
            >= 400 and < 500 => JiraError.Validation(ReadFieldErrors(body)),
            _ => JiraError.Unavailable,
        };
    }

    /// <summary>Jira's <c>errors</c> object as plain-text messages; anything unreadable yields no fields.</summary>
    public static IReadOnlyDictionary<string, string> ReadFieldErrors(ReadOnlySpan<byte> body) {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.IsEmpty) {
            return fields;
        }

        try {
            var errorBody = JsonSerializer.Deserialize(body, JiraClientJsonContext.Default.JiraErrorBody);
            foreach (var (name, message) in errorBody?.Errors ?? []) {
                if (message.ValueKind == JsonValueKind.String && fields.Count < 50) {
                    fields[PlainText(name, 100)] = PlainText(message.GetString()!, FieldMessageMaxLength);
                }
            }
        } catch (JsonException) {
            fields.Clear();
        }

        return fields;
    }

    /// <summary>
    /// Finds the story points and sprint custom fields in <c>GET /rest/api/3/field</c> (parity with the original's
    /// discovery: schema type first, then a field named like "Story Points"). <c>customfield_10020</c> is never
    /// assumed to be points.
    /// </summary>
    public static JiraFieldIds DiscoverFields(IEnumerable<JiraField> fields) {
        ArgumentNullException.ThrowIfNull(fields);
        string? pointsBySchema = null, pointsByName = null, sprint = null;
        foreach (var field in fields) {
            if (field.Id is null || !CustomFieldId().IsMatch(field.Id)) {
                continue;
            }

            var custom = field.Schema?.Custom?.ToLowerInvariant() ?? "";
            if (pointsBySchema is null && custom.Contains("story-points", StringComparison.Ordinal)) {
                pointsBySchema = field.Id;
            }
            if (pointsByName is null && field.Name is not null && StoryPointsName().IsMatch(field.Name)) {
                pointsByName = field.Id;
            }
            if (sprint is null && custom.Contains("gh-sprint", StringComparison.Ordinal)) {
                sprint = field.Id;
            }
        }

        return new JiraFieldIds(pointsBySchema ?? pointsByName, sprint);
    }

    /// <summary>The JQL reference of a custom field, <c>cf[10016]</c> for <c>customfield_10016</c>; names like
    /// "Story Points" are never used, because they differ per site and language.</summary>
    public static string JqlFieldRef(string customFieldId) {
        ArgumentNullException.ThrowIfNull(customFieldId);
        return CustomFieldId().IsMatch(customFieldId)
            ? $"cf[{customFieldId["customfield_".Length..]}]"
            : throw new ArgumentException("Not a custom field id.", nameof(customFieldId));
    }

    public static bool IsCustomFieldId(string? value) => value is not null && CustomFieldId().IsMatch(value);

    public static bool IsIssueKey(string? value) => value is not null && IssueKey().IsMatch(value);

    /// <summary>The issue's points from the configured field only; numbers and numeric strings are accepted.</summary>
    public static decimal? ReadPoints(JiraIssue issue, string? pointsFieldId) {
        ArgumentNullException.ThrowIfNull(issue);
        if (pointsFieldId is null || issue.Fields?.Extra is not { } extra || !extra.TryGetValue(pointsFieldId, out var value)) {
            return null;
        }

        return value.ValueKind switch {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// The issue's current sprint (port of the original's <c>createGetSprintMeta</c>): the last non-closed entry of
    /// the sprint field, else the last closed one. Objects and legacy <c>"com.atlassian…[id=1,state=…,name=…]"</c>
    /// strings are both read. Without a known sprint field every custom field is scanned, as the original did.
    /// </summary>
    public static JiraSprintRef? ReadSprint(JiraIssue issue, string? sprintFieldId, IReadOnlyDictionary<long, JiraSprint>? boardSprints = null) {
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.Fields?.Extra is not { } extra) {
            return null;
        }

        foreach (var allowClosed in (ReadOnlySpan<bool>)[false, true]) {
            if (sprintFieldId is not null) {
                if (extra.TryGetValue(sprintFieldId, out var value) && ReadSprintValue(value, allowClosed, boardSprints) is { } found) {
                    return found;
                }
                continue;
            }

            foreach (var (key, value) in extra) {
                if (key.StartsWith("customfield_", StringComparison.Ordinal)
                    && ReadSprintValue(value, allowClosed, boardSprints) is { } found) {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every sprint the issue's sprint field holds, in field order: objects and legacy <c>"com.atlassian…[id=1,…]"</c>
    /// strings alike, skipping entries without an id, a name or a known state. Legacy strings carry no start. Without a
    /// known sprint field the first custom field that holds sprints is read, as <see cref="ReadSprint"/> scans.
    /// </summary>
    public static IReadOnlyList<SprintMark> ReadSprintMarks(JiraIssue issue, string? sprintFieldId) {
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.Fields?.Extra is not { } extra) {
            return [];
        }
        if (sprintFieldId is not null) {
            return extra.TryGetValue(sprintFieldId, out var value) ? ReadMarks(value) : [];
        }

        foreach (var (key, value) in extra) {
            if (key.StartsWith("customfield_", StringComparison.Ordinal) && ReadMarks(value) is { Count: > 0 } marks) {
                return marks;
            }
        }
        return [];
    }

    /// <summary>
    /// What the sprint classifier needs of an issue: the status category, when it was done (the resolution date, else
    /// the status category change of a done issue), its points and its sprint marks.
    /// </summary>
    public static IssueFacts ToIssueFacts(JiraIssue issue, JiraTeamConfig config) {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(config);
        var fields = issue.Fields;
        var category = fields?.Status?.StatusCategory?.Key switch {
            "done" => WorkCategory.Done,
            "indeterminate" => WorkCategory.InProgress,
            _ => WorkCategory.ToDo,
        };
        long? doneAt = TryParseDate(fields?.Resolutiondate, out var resolved)
            ? resolved
            : category == WorkCategory.Done && TryParseDate(fields?.Statuscategorychangedate, out var changed) ? changed : null;
        return new IssueFacts(
            issue.Key ?? "",
            category,
            doneAt,
            ReadPoints(issue, config.PointsFieldId),
            ReadSprintMarks(issue, config.SprintFieldId));
    }

    /// <summary>
    /// Sprint bucket order (port of <c>jiraBrowseSprintBuckets.ts</c>): buckets with a parseable start date first,
    /// by date; then those with an unparseable one; then those with none; ties by name with <see cref="NaturalCompare"/>.
    /// </summary>
    public static int CompareSprints(string nameA, string? startA, string nameB, string? startB) {
        var aHas = !string.IsNullOrEmpty(startA);
        var bHas = !string.IsNullOrEmpty(startB);
        if (aHas && bHas) {
            var aOk = TryParseDate(startA, out var ta);
            var bOk = TryParseDate(startB, out var tb);
            if (aOk && bOk && ta != tb) {
                return ta.CompareTo(tb);
            }
            if (aOk != bOk) {
                return aOk ? -1 : 1;
            }
        } else if (aHas != bHas) {
            return aHas ? -1 : 1;
        }

        return NaturalCompare.Compare(nameA, nameB);
    }

    public static IComparer<JiraSprint> SprintOrder { get; } =
        Comparer<JiraSprint>.Create((a, b) => CompareSprints(a.Name ?? "", a.StartDate, b.Name ?? "", b.StartDate));

    /// <summary>Jira and agile dates (<c>2026-01-26T19:00:00.000+1000</c>, <c>…+10:00</c>, <c>…Z</c>) as UTC milliseconds.</summary>
    public static bool TryParseDate(string? value, out long unixMilliseconds) {
        unixMilliseconds = 0;
        if (string.IsNullOrEmpty(value)) {
            return false;
        }

        var normalized = CompactOffset().Replace(value, "$1:$2");
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            || !IsoDate().IsMatch(normalized)) {
            return false;
        }

        unixMilliseconds = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    private static JiraSprintRef? ReadSprintValue(JsonElement value, bool allowClosed, IReadOnlyDictionary<long, JiraSprint>? boardSprints) {
        if (value.ValueKind == JsonValueKind.Array) {
            for (var i = value.GetArrayLength() - 1; i >= 0; i--) {
                if (ReadSprintItem(value[i], allowClosed, boardSprints) is { } found) {
                    return found;
                }
            }
            return null;
        }

        return ReadSprintItem(value, allowClosed, boardSprints);
    }

    private static JiraSprintRef? ReadSprintItem(JsonElement item, bool allowClosed, IReadOnlyDictionary<long, JiraSprint>? boardSprints) {
        switch (item.ValueKind) {
            case JsonValueKind.Object: {
                var state = item.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (!allowClosed && state == "closed") {
                    return null;
                }

                long? id = item.TryGetProperty("id", out var idElement) ? ReadLong(idElement) : null;
                var known = id is { } sprintId && boardSprints is not null && boardSprints.TryGetValue(sprintId, out var k) ? k : null;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    ? n.ToString()
                    : known?.Name;
                var start = item.TryGetProperty("startDate", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } date
                    ? date
                    : known?.StartDate;
                return string.IsNullOrEmpty(name) ? null : new JiraSprintRef(name, start, id);
            }
            case JsonValueKind.String: {
                var text = item.GetString()!;
                var stateMatch = LegacyState().Match(text);
                if (!allowClosed && stateMatch.Success && stateMatch.Groups[1].Value.Trim().Equals("closed", StringComparison.OrdinalIgnoreCase)) {
                    return null;
                }

                var nameMatch = LegacyName().Match(text);
                if (nameMatch.Success) {
                    return new JiraSprintRef(nameMatch.Groups[1].Value.Trim(), null, null);
                }

                var idMatch = LegacyId().Match(text);
                if (idMatch.Success && long.TryParse(idMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var legacyId)
                    && boardSprints is not null && boardSprints.TryGetValue(legacyId, out var sprint) && sprint.Name is not null) {
                    return new JiraSprintRef(sprint.Name, sprint.StartDate, legacyId);
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static List<SprintMark> ReadMarks(JsonElement value) {
        var marks = new List<SprintMark>();
        if (value.ValueKind == JsonValueKind.Array) {
            foreach (var item in value.EnumerateArray()) {
                if (ReadMark(item) is { } mark) {
                    marks.Add(mark);
                }
            }
        } else if (ReadMark(value) is { } mark) {
            marks.Add(mark);
        }
        return marks;
    }

    private static SprintMark? ReadMark(JsonElement item) {
        switch (item.ValueKind) {
            case JsonValueKind.Object: {
                long? id = item.TryGetProperty("id", out var idElement) ? ReadLong(idElement) : null;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind is JsonValueKind.String or JsonValueKind.Number ? n.ToString() : null;
                var phase = item.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? ParsePhase(s.GetString()) : null;
                long? start = item.TryGetProperty("startDate", out var d) && d.ValueKind == JsonValueKind.String && TryParseDate(d.GetString(), out var at) ? at : null;
                return id is { } sprintId && !string.IsNullOrEmpty(name) && phase is { } known ? new SprintMark(sprintId, name, known, start) : null;
            }
            case JsonValueKind.String: {
                var text = item.GetString()!;
                var idMatch = LegacyId().Match(text);
                var nameMatch = LegacyName().Match(text);
                var stateMatch = LegacyState().Match(text);
                return idMatch.Success && long.TryParse(idMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var legacyId)
                    && nameMatch.Success && nameMatch.Groups[1].Value.Trim() is { Length: > 0 } legacyName
                    && stateMatch.Success && ParsePhase(stateMatch.Groups[1].Value.Trim()) is { } legacyPhase
                    ? new SprintMark(legacyId, legacyName, legacyPhase, null)
                    : null;
            }
            default:
                return null;
        }
    }

    private static SprintPhase? ParsePhase(string? state) => state?.ToLowerInvariant() switch {
        "active" => SprintPhase.Active,
        "closed" => SprintPhase.Closed,
        "future" => SprintPhase.Future,
        _ => null,
    };

    private static long? ReadLong(JsonElement element) => element.ValueKind switch {
        JsonValueKind.Number when element.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(element.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string PlainText(string value, int maxLength) {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var rune in value.EnumerateRunes()) {
            if (builder.Length + rune.Utf16SequenceLength > maxLength) {
                break;
            }
            if (Rune.IsControl(rune)) {
                builder.Append(' ');
            } else {
                builder.Append(rune.ToString());
            }
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"^customfield_[0-9]{1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CustomFieldId();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"story\s*points?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StoryPointsName();

    [GeneratedRegex(@"state=([^,\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyState();

    [GeneratedRegex(@"name=([^,\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyName();

    [GeneratedRegex(@"id=([0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyId();

    [GeneratedRegex(@"([+-][0-9]{2})([0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CompactOffset();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();
}

// Jira response documents: only the members we read. Unknown members are skipped (JiraClientJsonContext).

public sealed record JiraErrorBody(IReadOnlyList<string>? ErrorMessages, Dictionary<string, JsonElement>? Errors);

public sealed record JiraUser(string? AccountId, string? DisplayName, string? EmailAddress);

public sealed record JiraFieldSchema(string? Type, string? Custom);

public sealed record JiraField(string? Id, string? Name, bool Custom, JiraFieldSchema? Schema);

public sealed record JiraBoardLocation(string? ProjectKey, long? ProjectId);

public sealed record JiraBoard(long Id, string? Name, string? Type, JiraBoardLocation? Location);

public sealed record JiraEstimationField(string? FieldId, string? DisplayName);

/// <param name="Type"><c>none</c>, <c>issueCount</c> or <c>field</c>; only Scrum boards send an estimation.</param>
public sealed record JiraEstimation(string? Type, JiraEstimationField? Field);

public sealed record JiraBoardConfiguration(JiraEstimation? Estimation);

/// <param name="Goal">Written by people: cached in memory only, never stored.</param>
public sealed record JiraSprint(
    long Id,
    string? Name,
    string? State,
    string? StartDate,
    string? EndDate,
    string? CompleteDate = null,
    string? Goal = null,
    long? OriginBoardId = null);

/// <summary>An Agile 1.0 page of <c>values</c> (boards, sprints). <see cref="Total"/> is absent on some sites and can be stale.</summary>
public sealed record JiraValuesPage<T>(int StartAt, int MaxResults, bool IsLast, IReadOnlyList<T>? Values, int? Total = null);

public sealed record JiraStatusCategory(string? Key, string? Name);

public sealed record JiraStatus(string? Id, string? Name, JiraStatusCategory? StatusCategory);

public sealed record JiraNamed(string? Id, string? Name);

public sealed record JiraIssueType(string? Id, string? Name, bool Subtask, long? HierarchyLevel);

public sealed record JiraParentFields(string? Summary, JiraStatus? Status, JiraIssueType? Issuetype);

public sealed record JiraParent(string? Id, string? Key, JiraParentFields? Fields);

public sealed record JiraProjectRef(string? Id, string? Key, string? Name);

public sealed record JiraAttachmentInfo(string? Id, string? Filename, string? MimeType, long Size, string? Created);

public sealed record JiraLinkType(string? Id, string? Name, string? Inward, string? Outward);

public sealed record JiraLinkedIssue(string? Id, string? Key, JiraParentFields? Fields);

public sealed record JiraIssueLink(string? Id, JiraLinkType? Type, JiraLinkedIssue? InwardIssue, JiraLinkedIssue? OutwardIssue);

public sealed record JiraComment(string? Id, JiraUser? Author, JsonElement? Body, string? Created, string? Updated);

public sealed record JiraCommentPage(int StartAt, int MaxResults, int Total, IReadOnlyList<JiraComment>? Comments);

/// <summary>The fields of an issue. Custom fields (points, sprint) have site-specific ids and land in <see cref="Extra"/>.</summary>
public sealed class JiraIssueFields {
    public string? Summary { get; init; }

    public JsonElement? Description { get; init; }

    public JiraStatus? Status { get; init; }

    public JiraNamed? Priority { get; init; }

    public JiraUser? Assignee { get; init; }

    public JiraUser? Reporter { get; init; }

    public JiraIssueType? Issuetype { get; init; }

    public JiraParent? Parent { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public IReadOnlyList<JiraAttachmentInfo>? Attachment { get; init; }

    public IReadOnlyList<JiraIssueLink>? Issuelinks { get; init; }

    public string? Created { get; init; }

    public string? Updated { get; init; }

    public string? Resolutiondate { get; init; }

    public string? Statuscategorychangedate { get; init; }

    public JiraProjectRef? Project { get; init; }

    public JiraCommentPage? Comment { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record JiraIssue(string? Id, string? Key, JiraIssueFields? Fields);

/// <summary>An Agile 1.0 page of issues (sprint issues, board backlog).</summary>
public sealed record JiraIssuePage(int StartAt, int MaxResults, int Total, IReadOnlyList<JiraIssue>? Issues);

/// <summary>A page of <c>POST /rest/api/3/search/jql</c>: token pagination, no total.</summary>
public sealed record JiraSearchPage(IReadOnlyList<JiraIssue>? Issues, string? NextPageToken, bool? IsLast);

public sealed record JiraAllowedValues<T>(IReadOnlyList<T>? AllowedValues);

public sealed record JiraEditMetaFields(JiraAllowedValues<JiraNamed>? Priority, JiraAllowedValues<JiraIssueType>? Issuetype);

public sealed record JiraEditMeta(JiraEditMetaFields? Fields);

public sealed record JiraTransitionTarget(string? Id, string? Name, JiraStatusCategory? StatusCategory);

public sealed record JiraTransition(string? Id, string? Name, JiraTransitionTarget? To);

public sealed record JiraTransitions(IReadOnlyList<JiraTransition>? Transitions);

public sealed record JiraProject(string? Id, string? Key, string? Name, IReadOnlyList<JiraIssueType>? IssueTypes);

public sealed record JiraLinkTypes(IReadOnlyList<JiraLinkType>? IssueLinkTypes);

/// <summary><c>GET /rest/api/3/issue/createmeta/{project}/issuetypes</c> (the successor of the deprecated createmeta).</summary>
public sealed record JiraCreateMetaIssueTypes(int StartAt, int MaxResults, long Total, IReadOnlyList<JiraIssueType>? IssueTypes);

public sealed record JiraCreatedIssue(string? Id, string? Key);

public sealed record JiraCreatedComment(string? Id);

// Request bodies.

public sealed record JiraSearchRequest(string Jql, IReadOnlyList<string> Fields, int MaxResults, string? NextPageToken);

public sealed record JiraIdRef(string Id);

public sealed record JiraTransitionRequest(JiraIdRef Transition);

public sealed record JiraMoveIssuesRequest(IReadOnlyList<string> Issues);

public sealed record JiraKeyRef(string Key);

public sealed record JiraNameRef(string Name);

public sealed record JiraLinkRequest(JiraNameRef Type, JiraKeyRef InwardIssue, JiraKeyRef OutwardIssue);

public sealed record JiraCommentRequest(JsonElement Body);
