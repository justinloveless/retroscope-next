using Retroscope.Fakes.FakeJira;

namespace Retroscope.Fakes.Host;

/// <summary>A small project for the e2e stack: one scrum board, an active and a future sprint, and a backlog.</summary>
public static class SeedData {
    public const string ProjectKey = "ACME";
    public const long BoardId = 1;

    public static void Apply(FakeJiraServer jira) {
        jira.AddBoard(BoardId, ProjectKey);
        jira.AddSprint(101, "Sprint 41", "active", "2026-10-06T09:00:00.000Z");
        jira.AddSprint(102, "Sprint 42", "future", null);

        jira.AddIssue("ACME-1", "Checkout: remember the last payment method", sprintId: 101, points: 5);
        jira.AddIssue("ACME-2", "Checkout: show the delivery estimate", sprintId: 101);
        jira.AddIssue("ACME-3", "Fix the rounding of discounts", sprintId: 101, points: 2, status: "Done");
        jira.AddIssue("ACME-4", "Onboarding: invite teammates from the first screen", sprintId: 102);
        jira.AddIssue("ACME-5", "Onboarding: sample board for new teams", sprintId: 102);
        jira.AddIssue("ACME-6", "Export a retro as Markdown");
        jira.AddIssue("ACME-7", "Dark mode for the email digest");
    }
}
