using System.Globalization;
using Retroscope.Core.Authz;
using Retroscope.Core.Sprints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

/// <param name="State"><c>active</c> or <c>closed</c>.</param>
public sealed record JiraReportSprintItem(long Id, string? Name, string State, string? StartDate, string? EndDate, string? CompleteDate);

/// <param name="Next">The name of the later sprint that holds unfinished work of a closed sprint; null for the backlog
/// or when the issue was done.</param>
public sealed record JiraReportRowView(
    string Key,
    string? Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    string? Assignee,
    decimal? Points,
    bool CarriedIn,
    string? Next);

/// <param name="Outcome"><c>done</c>, <c>doneAfter</c> or <c>notDone</c>.</param>
public sealed record JiraReportGroupView(string Outcome, int Count, decimal? Points, IReadOnlyList<JiraReportRowView> Issues);

/// <param name="Groups">Always the three outcomes, in order.</param>
public sealed record JiraSprintReportBody(long SprintId, string? Goal, bool PointsKnown, bool Truncated, IReadOnlyList<JiraReportGroupView> Groups);

/// <param name="HasMoreSprints">Older closed sprints can be listed by asking for more.</param>
/// <param name="Empty"><c>no_board</c>, <c>no_sprints</c> or null.</param>
public sealed record JiraSprintReportView(IReadOnlyList<JiraReportSprintItem> Sprints, bool HasMoreSprints, string? Empty, JiraSprintReportBody? Report);

public static partial class JiraEndpoints {
    private static async Task<IResult> SprintReportAsync(
        HttpContext context, Guid teamId, string? sprint, string? sprints, JiraGate gate, SprintReportService reports) {
        var (call, refusal) = await gate.CheckAsync(context, teamId, Capability.Subscribe, needsConfig: true);
        if (refusal is not null) {
            return refusal;
        }
        long? sprintId = null;
        if (sprint is not null) {
            if (!NumericId().IsMatch(sprint) || !long.TryParse(sprint, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) {
                return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["sprint"] = "invalid" });
            }
            sprintId = parsed;
        }
        var closedSprints = SprintReportService.DefaultClosedSprints;
        if (sprints is not null) {
            if (!NumericId().IsMatch(sprints) || !int.TryParse(sprints, NumberStyles.None, CultureInfo.InvariantCulture, out closedSprints)
                || closedSprints is < 1 or > SprintReportService.PickerSize) {
                return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["sprints"] = "invalid" });
            }
        }

        return await GuardAsync(async () => {
            var result = await reports.GetAsync(call!.Team, sprintId, closedSprints, context.RequestAborted);
            return Ok(SprintReportView(result), JiraHttpJsonContext.Default.JiraSprintReportView);
        });
    }

    private static JiraSprintReportView SprintReportView(SprintReportResult result) => new(
        [.. result.Sprints.Select(s => new JiraReportSprintItem(
            s.Id, s.Name, SprintReportService.IsClosed(s) ? "closed" : "active", s.StartDate, s.EndDate, s.CompleteDate))],
        result.HasMoreSprints,
        result.Empty switch {
            ReportEmpty.NoBoard => "no_board",
            ReportEmpty.NoSprints => "no_sprints",
            _ => null,
        },
        result.Report is { } report
            ? new JiraSprintReportBody(
                report.SprintId,
                report.Goal,
                report.PointsKnown,
                report.Truncated,
                [.. report.Totals.Select(total => new JiraReportGroupView(
                    OutcomeName(total.Outcome),
                    total.Count,
                    total.Points,
                    [.. report.Rows.Where(row => row.Verdict.Outcome == total.Outcome).Select(ReportRowView)]))])
            : null);

    private static JiraReportRowView ReportRowView(ReportRow row) => new(
        row.Key,
        row.Summary,
        row.IssueType,
        row.Status,
        row.StatusCategory,
        row.Assignee,
        row.Points,
        row.Verdict.CarriedIn,
        row.Verdict.MovedTo?.Name);

    private static string OutcomeName(SprintOutcome outcome) => outcome switch {
        SprintOutcome.Done => "done",
        SprintOutcome.DoneAfter => "doneAfter",
        _ => "notDone",
    };
}
