using FsCheck.Xunit;
using Retroscope.Core.Sprints;

namespace Retroscope.Core.Tests.Sprints;

[Trait("Category", "Sprints")]
public sealed class SprintClassifierTests {
    private const long Start = 1_000_000;
    private const long Close = 2_000_000;

    private static readonly ReportedSprint.Closed Closed = new(42, Start, Close);
    private static readonly ReportedSprint.Active Active = new(42, Start);
    private static readonly SprintMark Self = new(42, "Sprint 42", SprintPhase.Closed, Start);
    private static readonly SprintMark Earlier = new(41, "Sprint 41", SprintPhase.Closed, Start - 1000);
    private static readonly SprintMark Next = new(43, "Sprint 43", SprintPhase.Active, Close + 10);
    private static readonly SprintMark Planned = new(44, "Sprint 44", SprintPhase.Future, null);

    [Fact]
    public void Work_done_by_the_close_is_done() {
        Assert.Equal(new IssueVerdict(SprintOutcome.Done, false, null), SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, Close - 1)));
        Assert.Equal(SprintOutcome.Done, SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, Close)).Outcome);
    }

    [Fact]
    public void Work_done_after_the_close_is_done_after_and_says_where_it_went() {
        var verdict = SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, Close + 1, Self, Next));

        Assert.Equal(new IssueVerdict(SprintOutcome.DoneAfter, false, Next), verdict);
    }

    [Fact]
    public void Unfinished_work_goes_to_the_earliest_later_sprint_or_back_to_the_backlog() {
        Assert.Equal(Next, SprintClassifier.Classify(Closed, Issue(WorkCategory.InProgress, null, Planned, Self, Next)).MovedTo);
        Assert.Equal(Planned, SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, Self, Planned)).MovedTo);
        Assert.Equal(new IssueVerdict(SprintOutcome.NotDone, false, null), SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, Self)));
    }

    [Fact]
    public void Future_sprints_without_a_start_are_ordered_by_id() {
        var later = Planned with { Id = 50, Name = "Sprint 50" };

        Assert.Equal(Planned, SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, later, Planned)).MovedTo);
    }

    [Fact]
    public void A_later_closed_sprint_counts_as_where_the_work_went() {
        var closedLater = new SprintMark(45, "Sprint 45", SprintPhase.Closed, Close + 5);

        Assert.Equal(closedLater, SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, Self, closedLater)).MovedTo);
    }

    [Fact]
    public void Done_work_without_a_date_is_judged_by_whether_a_later_sprint_holds_it() {
        Assert.Equal(new IssueVerdict(SprintOutcome.Done, false, null), SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, null, Self)));
        Assert.Equal(new IssueVerdict(SprintOutcome.DoneAfter, false, Next), SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, null, Self, Next)));
    }

    [Fact]
    public void An_active_sprint_has_only_done_and_not_done_and_no_destination() {
        Assert.Equal(new IssueVerdict(SprintOutcome.Done, false, null), SprintClassifier.Classify(Active, Issue(WorkCategory.Done, Close + 999, Self, Planned)));
        Assert.Equal(new IssueVerdict(SprintOutcome.NotDone, false, null), SprintClassifier.Classify(Active, Issue(WorkCategory.InProgress, null, Self, Planned)));
    }

    [Fact]
    public void Work_from_an_earlier_closed_sprint_is_carried_in() {
        Assert.True(SprintClassifier.Classify(Closed, Issue(WorkCategory.Done, Close - 1, Earlier, Self)).CarriedIn);
        Assert.True(SprintClassifier.Classify(Active, Issue(WorkCategory.ToDo, null, Earlier, Self)).CarriedIn);
    }

    [Fact]
    public void Marks_that_cannot_be_dated_or_are_not_closed_are_not_carried_in() {
        var undated = Earlier with { StartedAt = null };
        var activeEarlier = Earlier with { Phase = SprintPhase.Active };

        Assert.False(SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, undated, Self)).CarriedIn);
        Assert.False(SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, activeEarlier, Self)).CarriedIn);
        Assert.False(SprintClassifier.Classify(new ReportedSprint.Closed(42, null, Close), Issue(WorkCategory.ToDo, null, Earlier)).CarriedIn);
    }

    [Fact]
    public void Legacy_marks_without_a_start_are_later_only_while_open() {
        var legacyClosed = new SprintMark(39, "Legacy", SprintPhase.Closed, null);
        var legacyFuture = new SprintMark(46, "Legacy next", SprintPhase.Future, null);

        Assert.Equal(new IssueVerdict(SprintOutcome.NotDone, false, null), SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, legacyClosed)));
        Assert.Equal(legacyFuture, SprintClassifier.Classify(Closed, Issue(WorkCategory.ToDo, null, legacyClosed, legacyFuture)).MovedTo);
    }

    [Fact]
    public void Totals_cover_every_outcome_in_order_with_points_only_when_known() {
        IssueFacts Pointed(decimal? points) => Issue(WorkCategory.Done, null) with { Points = points };
        var rows = new List<(IssueFacts, IssueVerdict)> {
            (Pointed(3), new IssueVerdict(SprintOutcome.Done, false, null)),
            (Pointed(null), new IssueVerdict(SprintOutcome.Done, false, null)),
            (Pointed(2.5m), new IssueVerdict(SprintOutcome.NotDone, true, null)),
        };

        Assert.Equal(
            [new OutcomeTotal(SprintOutcome.Done, 2, 3), new OutcomeTotal(SprintOutcome.DoneAfter, 0, 0), new OutcomeTotal(SprintOutcome.NotDone, 1, 2.5m)],
            SprintClassifier.Totals(rows, pointsKnown: true));
        Assert.Equal(
            [new OutcomeTotal(SprintOutcome.Done, 2, null), new OutcomeTotal(SprintOutcome.DoneAfter, 0, null), new OutcomeTotal(SprintOutcome.NotDone, 1, null)],
            SprintClassifier.Totals(rows, pointsKnown: false));
    }

    [Property(MaxTest = 300)]
    public bool Totals_partition_the_issues_and_sum_their_rows(int seed, bool closed, bool pointsKnown) {
        var (sprint, issues) = Generate(seed, closed);
        var rows = issues.Select(issue => (issue, SprintClassifier.Classify(sprint, issue))).ToList();

        var totals = SprintClassifier.Totals(rows, pointsKnown);

        return totals.Select(t => t.Outcome).SequenceEqual([SprintOutcome.Done, SprintOutcome.DoneAfter, SprintOutcome.NotDone])
            && totals.Sum(t => t.Count) == issues.Count
            && totals.All(t => t.Count == rows.Count(r => r.Item2.Outcome == t.Outcome))
            && totals.All(t => pointsKnown
                ? t.Points == rows.Where(r => r.Item2.Outcome == t.Outcome).Sum(r => r.issue.Points ?? 0)
                : t.Points is null);
    }

    [Property(MaxTest = 300)]
    public bool An_active_sprint_never_reports_done_after_or_a_destination(int seed) {
        var (sprint, issues) = Generate(seed, closed: false);

        return issues.Select(issue => SprintClassifier.Classify(sprint, issue))
            .All(v => v.Outcome != SprintOutcome.DoneAfter && v.MovedTo is null);
    }

    [Property(MaxTest = 300)]
    public bool Only_unfinished_or_late_work_of_a_closed_sprint_names_a_destination(int seed) {
        var (sprint, issues) = Generate(seed, closed: true);

        return issues.Select(issue => SprintClassifier.Classify(sprint, issue))
            .All(v => v.MovedTo is null || v.Outcome != SprintOutcome.Done && v.MovedTo.Id != sprint.Id);
    }

    [Property(MaxTest = 300)]
    public bool Verdicts_do_not_depend_on_the_order_of_the_sprint_field(int seed, bool closed) {
        var (sprint, issues) = Generate(seed, closed);
        var shuffle = new Random(seed ^ 0x5eed);

        return issues.All(issue => SprintClassifier.Classify(sprint, issue)
            == SprintClassifier.Classify(sprint, issue with { Sprints = [.. issue.Sprints.OrderBy(_ => shuffle.Next())] }));
    }

    private static IssueFacts Issue(WorkCategory category, long? doneAt, params SprintMark[] marks) =>
        new("RS-1", category, doneAt, null, marks);

    private static (ReportedSprint Sprint, List<IssueFacts> Issues) Generate(int seed, bool closed) {
        var random = new Random(seed);
        long? start = random.Next(5) == 0 ? null : Start;
        ReportedSprint sprint = closed ? new ReportedSprint.Closed(42, start, Close) : new ReportedSprint.Active(42, start);
        var issues = new List<IssueFacts>();
        for (var i = random.Next(12); i > 0; i--) {
            var marks = new List<SprintMark>();
            var ids = Enumerable.Range(30, 20).OrderBy(_ => random.Next()).Take(random.Next(4)).ToList();
            if (random.Next(3) > 0) {
                ids.Add(42);
            }
            foreach (var id in ids) {
                var phase = (SprintPhase)random.Next(3);
                long? markStart = random.Next(4) == 0 ? null : Start + random.Next(-3, 4) * 1000L;
                marks.Add(new SprintMark(id, $"Sprint {id}", phase, phase == SprintPhase.Future && random.Next(2) == 0 ? null : markStart));
            }
            long? doneAt = random.Next(3) == 0 ? null : Close + random.Next(-2, 3) * 1000L;
            decimal? points = random.Next(4) == 0 ? null : random.Next(14);
            issues.Add(new IssueFacts($"RS-{i}", (WorkCategory)random.Next(3), doneAt, points, marks));
        }
        return (sprint, issues);
    }
}
