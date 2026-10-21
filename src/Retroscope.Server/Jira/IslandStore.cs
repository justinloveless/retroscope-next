using System.Globalization;
using Retroscope.Core.Adf;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;

namespace Retroscope.Server.Jira;

public sealed record RenderedComment(string Id, string? Author, string? Created, Doc Body);

/// <summary>An issue as the drawer shows it: Jira's fields, the description split into editable markup and islands,
/// the newest comments rendered, and the attachments the description and the proxy may refer to.</summary>
public sealed record IssueDetail(
    JiraIssue Issue,
    JiraTeamConfig Config,
    string Updated,
    AdfDescription Description,
    IReadOnlyList<AdfAttachment> Attachments,
    IReadOnlyList<RenderedComment> Comments,
    int CommentTotal);

/// <summary>The original ADF of a description's islands, for one version (<c>updated</c>) of the issue.</summary>
public sealed record IslandSet(IReadOnlyDictionary<string, string> Islands, bool Editable);

/// <summary>
/// The 60 s issue detail cache (DESIGN §f "Editing with opaque islands"). A description's islands are kept per
/// <c>(team, key, updated)</c>, so a save can splice the original ADF subtrees back in; when that version is no
/// longer cached the issue is fetched again, and a save against a version Jira has moved past is refused as stale.
/// </summary>
public sealed class IslandStore(JiraClient client, Credentials credentials, JiraCache cache) {
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public const int FirstComments = 20;

    public static string DetailsTag(TeamId team) => $"{team.Value:N}:details";

    public Task<IssueDetail> GetAsync(TeamId team, string issueKey, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            DetailKey(team, issueKey),
            Ttl,
            async () => {
                var detail = await FetchAsync(team, issueKey);
                return (detail, Estimate(detail), Tags(team, issueKey));
            },
            cancellationToken);

    /// <summary>The islands of the description as it was at <paramref name="updated"/>; null when Jira's issue has
    /// moved on since (the write is stale).</summary>
    public async Task<IslandSet?> IslandsAsync(TeamId team, string issueKey, string updated, CancellationToken cancellationToken) {
        if (cache.TryGet<IslandSet>(IslandsKey(team, issueKey, updated), out var cached)) {
            return cached;
        }
        cache.Remove(DetailKey(team, issueKey));
        var detail = await GetAsync(team, issueKey, cancellationToken);
        return detail.Updated == updated ? new IslandSet(detail.Description.Islands, detail.Description.Editable) : null;
    }

    /// <summary>The issue's attachments, which an attachment request must be one of.</summary>
    public async Task<IReadOnlyList<AdfAttachment>> AttachmentsAsync(TeamId team, string issueKey, CancellationToken cancellationToken) =>
        (await GetAsync(team, issueKey, cancellationToken)).Attachments;

    public static IReadOnlyList<string> DetailFields(JiraTeamConfig config) {
        var fields = new List<string> {
            "summary", "description", "status", "priority", "assignee", "reporter", "issuetype", "parent", "labels",
            "attachment", "issuelinks", "created", "updated", "project", "comment",
        };
        if (config.PointsFieldId is { } points) {
            fields.Add(points);
        }
        if (config.SprintFieldId is { } sprint) {
            fields.Add(sprint);
        }
        return fields;
    }

    public static IReadOnlyList<AdfAttachment> Attachments(JiraIssue issue) =>
        [.. (issue.Fields?.Attachment ?? [])
            .Where(attachment => attachment.Id is not null)
            .Select(attachment => new AdfAttachment(
                attachment.Id!,
                attachment.Filename ?? "",
                attachment.MimeType ?? "application/octet-stream",
                JiraMap.TryParseDate(attachment.Created, out var created) ? DateTimeOffset.FromUnixTimeMilliseconds(created) : DateTimeOffset.MinValue))];

    public static RenderedComment Render(JiraComment comment, IReadOnlyList<AdfAttachment> attachments) =>
        new(
            comment.Id ?? "",
            comment.Author?.DisplayName,
            comment.Created,
            comment.Body is { } body ? AdfToDoc.Render(body, attachments) : new Doc([]));

    private async Task<IssueDetail> FetchAsync(TeamId team, string issueKey) {
        var config = await credentials.LoadAsync(team, CancellationToken.None) ?? throw new JiraException(JiraError.NotConfigured);
        var issue = await client.GetIssueAsync(team, issueKey, DetailFields(config), CancellationToken.None);
        var fields = issue.Fields;
        var attachments = Attachments(issue);
        var description = fields?.Description is { } adf
            ? AdfToDoc.Description(adf, attachments)
            : new AdfDescription(new Doc([]), new Dictionary<string, string>(), editable: true);
        // The issue's comment field lists the oldest first; the drawer shows the newest first.
        var comments = (fields?.Comment?.Comments ?? [])
            .Reverse()
            .Take(FirstComments)
            .Select(comment => Render(comment, attachments))
            .ToList();
        var updated = fields?.Updated ?? "";
        var detail = new IssueDetail(
            issue,
            config,
            updated,
            description,
            attachments,
            comments,
            Math.Max(fields?.Comment?.Total ?? 0, comments.Count));
        cache.Set(
            IslandsKey(team, issueKey, updated),
            new IslandSet(description.Islands, description.Editable),
            64L + description.Islands.Sum(island => 2L * (island.Key.Length + island.Value.Length)),
            Ttl,
            [JiraCache.TeamTag(team)]);
        return detail;
    }

    private static string DetailKey(TeamId team, string issueKey) => JiraCache.Key(team, "detail", issueKey);

    private static string IslandsKey(TeamId team, string issueKey, string updated) =>
        JiraCache.Key(team, "islands", string.Create(CultureInfo.InvariantCulture, $"{issueKey}@{updated}"));

    private static IReadOnlyCollection<string> Tags(TeamId team, string issueKey) =>
        [JiraCache.TeamTag(team), JiraCache.IssueTag(team, issueKey), DetailsTag(team)];

    // The rendered tree is several times its markup; the islands keep the original ADF text.
    private static long Estimate(IssueDetail detail) =>
        1024L
        + 4L * detail.Description.Islands.Sum(island => island.Value.Length)
        + 2048L * detail.Comments.Count
        + 256L * detail.Attachments.Count;
}
