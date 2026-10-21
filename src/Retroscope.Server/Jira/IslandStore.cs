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
/// The 60 s issue detail cache (DESIGN §f "Editing with opaque islands"): each entry keeps the description's islands
/// (their original ADF) with the <c>updated</c> they belong to. A description save reads the issue from Jira again
/// first, so an edit made in Jira during those 60 s is never overwritten: the save is refused as stale unless Jira's
/// <c>updated</c> is still the one the edit started from, and the islands it splices back are that version's.
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

    /// <summary>The islands of the description as Jira has it now, when that is still the version at
    /// <paramref name="updated"/>; null when the issue has moved on since (the write is stale).</summary>
    public async Task<IslandSet?> IslandsAsync(TeamId team, string issueKey, string updated, CancellationToken cancellationToken) {
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
        return new IssueDetail(
            issue,
            config,
            fields?.Updated ?? "",
            description,
            attachments,
            comments,
            Math.Max(fields?.Comment?.Total ?? 0, comments.Count));
    }

    private static string DetailKey(TeamId team, string issueKey) => JiraCache.Key(team, "detail", issueKey);

    private static IReadOnlyCollection<string> Tags(TeamId team, string issueKey) =>
        [JiraCache.TeamTag(team), JiraCache.IssueTag(team, issueKey), DetailsTag(team)];

    // The rendered tree is several times its markup; the islands keep the original ADF text.
    private static long Estimate(IssueDetail detail) =>
        1024L
        + 4L * detail.Description.Islands.Sum(island => island.Value.Length)
        + 2048L * detail.Comments.Count
        + 256L * detail.Attachments.Count;
}
