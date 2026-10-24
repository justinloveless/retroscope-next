namespace Retroscope.Core.Sprints;

/// <summary>
/// The sprint a report is about. Only a closed sprint has a completion instant, so "done after the sprint" cannot be
/// expressed for an active one. Instants are UTC Unix milliseconds.
/// </summary>
public abstract record ReportedSprint {
    private ReportedSprint(long id, long? startedAt) {
        Id = id;
        StartedAt = startedAt;
    }

    public long Id { get; }

    public long? StartedAt { get; }

    public sealed record Active(long Id, long? StartedAt) : ReportedSprint(Id, StartedAt);

    public sealed record Closed(long Id, long? StartedAt, long CompletedAt) : ReportedSprint(Id, StartedAt);
}

public enum SprintPhase {
    Future,
    Active,
    Closed,
}

/// <summary>One entry of an issue's sprint field: a sprint the issue is in, or was in when that sprint closed.</summary>
public sealed record SprintMark(long Id, string Name, SprintPhase Phase, long? StartedAt);

public enum WorkCategory {
    ToDo,
    InProgress,
    Done,
}

/// <param name="DoneAt">When the issue reached done: its resolution date, else its status category change when it is done.</param>
public sealed record IssueFacts(string Key, WorkCategory Category, long? DoneAt, decimal? Points, IReadOnlyList<SprintMark> Sprints);

public enum SprintOutcome {
    Done,
    DoneAfter,
    NotDone,
}

/// <param name="CarriedIn">The issue was also in a closed sprint that started before this one.</param>
/// <param name="MovedTo">For a closed sprint and an outcome other than done: the earliest later sprint that holds the
/// issue, or null when the unfinished work went back to the backlog. Always null for an active sprint.</param>
public sealed record IssueVerdict(SprintOutcome Outcome, bool CarriedIn, SprintMark? MovedTo);

public sealed record OutcomeTotal(SprintOutcome Outcome, int Count, decimal? Points);

/// <summary>Sorts a sprint's issues into what got done in it, what got done after it closed, and what did not.</summary>
public static class SprintClassifier {
    private static readonly SprintOutcome[] Outcomes = [SprintOutcome.Done, SprintOutcome.DoneAfter, SprintOutcome.NotDone];

    /// <summary>Total and deterministic: never throws, and the order of the issue's sprint marks does not matter.</summary>
    public static IssueVerdict Classify(ReportedSprint sprint, IssueFacts issue) {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentNullException.ThrowIfNull(issue);
        SprintMark? later = null;
        var carriedIn = false;
        foreach (var mark in issue.Sprints) {
            if (mark.Id == sprint.Id) {
                continue;
            }
            var order = CompareStarts(mark, sprint);
            if (mark.Phase == SprintPhase.Closed && order < 0) {
                carriedIn = true;
            }
            if ((mark.Phase != SprintPhase.Closed || order > 0) && (later is null || Earlier(mark, later))) {
                later = mark;
            }
        }

        var done = issue.Category == WorkCategory.Done;
        var outcome = sprint switch {
            ReportedSprint.Active => done ? SprintOutcome.Done : SprintOutcome.NotDone,
            _ when !done => SprintOutcome.NotDone,
            ReportedSprint.Closed closed when issue.DoneAt is { } at => at <= closed.CompletedAt ? SprintOutcome.Done : SprintOutcome.DoneAfter,
            // Done with no instant: closing a sprint moves only unfinished work on, so a later sprint means it was unfinished then.
            _ => later is null ? SprintOutcome.Done : SprintOutcome.DoneAfter,
        };
        var movedTo = sprint is ReportedSprint.Closed && outcome != SprintOutcome.Done ? later : null;
        return new IssueVerdict(outcome, carriedIn, movedTo);
    }

    /// <summary>Exactly one total per outcome, in enum order. Points are null unless <paramref name="pointsKnown"/>;
    /// an issue without points then counts as 0.</summary>
    public static IReadOnlyList<OutcomeTotal> Totals(IEnumerable<(IssueFacts Issue, IssueVerdict Verdict)> rows, bool pointsKnown) {
        ArgumentNullException.ThrowIfNull(rows);
        var counts = new int[Outcomes.Length];
        var points = new decimal[Outcomes.Length];
        foreach (var (issue, verdict) in rows) {
            var at = (int)verdict.Outcome;
            counts[at]++;
            points[at] += issue.Points ?? 0;
        }
        return [.. Outcomes.Select(o => new OutcomeTotal(o, counts[(int)o], pointsKnown ? points[(int)o] : null))];
    }

    // Null when either start is unknown: such a mark is neither before nor after the sprint by date.
    private static int? CompareStarts(SprintMark mark, ReportedSprint sprint) =>
        mark.StartedAt is { } a && sprint.StartedAt is { } b ? a.CompareTo(b) : null;

    private static bool Earlier(SprintMark a, SprintMark b) =>
        (a.StartedAt ?? long.MaxValue, a.Id).CompareTo((b.StartedAt ?? long.MaxValue, b.Id)) < 0;
}
