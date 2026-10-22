using System.Globalization;
using System.Text.Json.Nodes;
using Retroscope.Fakes.FakeJira;

namespace Retroscope.Fakes.Host;

/// <summary>
/// A small project for the e2e stack: one scrum board, an active and a future sprint, and a backlog. The e2e specs edit
/// issues, so each Playwright browser project gets its own Jira project and board as well (<see cref="Playgrounds"/>),
/// and the four never touch each other's issues while they run in parallel.
/// </summary>
public static class SeedData {
    public const string ProjectKey = "ACME";
    public const long BoardId = 1;

    /// <summary>Project key → board id, one per browser project of tests/e2e/playwright.config.js.</summary>
    public static readonly IReadOnlyDictionary<string, long> Playgrounds = new Dictionary<string, long> {
        ["CHR"] = 11,
        ["FFX"] = 12,
        ["WBK"] = 13,
        ["MWK"] = 14,
    };

    // A 1×1 PNG, enough for the drawer's attachment thumbnail and the server's image sniffing.
    private static readonly byte[] Screenshot = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

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

        var attachment = 20000;
        foreach (var (project, board) in Playgrounds) {
            jira.AddBoard(board, project);
            jira.AddIssue($"{project}-1", "Checkout: remember the last payment method", sprintId: 101, points: 5);
            jira.AddIssue($"{project}-2", "Checkout: show the delivery estimate", sprintId: 101);
            jira.AddIssue($"{project}-3", "Fix the rounding of discounts", sprintId: 101, points: 2, status: "Done");
            jira.AddIssue($"{project}-4", "Onboarding: invite teammates from the first screen", sprintId: 102);
            jira.AddIssue($"{project}-5", "Onboarding: sample board for new teams", sprintId: 102);
            jira.AddIssue($"{project}-6", "Export a retro as Markdown");
            var rich = jira.AddIssue($"{project}-7", "Pay with a saved card");
            jira.AddIssue($"{project}-8", "Points land in Jira");
            jira.AddIssue($"{project}-9", "Preview me while spotlighting");
            var hostile = jira.AddIssue($"{project}-10", "<img src=x onerror=alert(10)> summary");
            var id = (++attachment).ToString(CultureInfo.InvariantCulture);
            jira.AddAttachment($"{project}-7", id, "screenshot.png", "image/png", Screenshot);
            rich["fields"]!["description"] = RichDescription();
            hostile["fields"]!["description"] = HostileDescription();
        }
    }

    /// <summary>Links and cards that must never become live, and markup-looking text (the XSS corpus, §j).</summary>
    private static JsonNode HostileDescription() => JsonNode.Parse("""
        {"type":"doc","version":1,"content":[
          {"type":"paragraph","content":[
            {"type":"text","text":"click me","marks":[{"type":"link","attrs":{"href":"javascript:alert(11)"}}]},
            {"type":"text","text":" <script>alert(12)</script> "},
            {"type":"text","text":"data","marks":[{"type":"link","attrs":{"href":"data:text/html,<script>alert(13)</script>"}}]},
            {"type":"inlineCard","attrs":{"url":"javascript:alert(14)"}}]},
          {"type":"paragraph","content":[{"type":"text","text":"<img src=x onerror=alert(15)>"}]}
        ]}
        """)!;

    /// <summary>A paragraph, a table and a screenshot (both islands for Retroscope's editor) and a list to edit.</summary>
    private static JsonNode RichDescription() => JsonNode.Parse("""
        {"type":"doc","version":1,"content":[
          {"type":"paragraph","content":[{"type":"text","text":"Customers "},{"type":"text","text":"pay with a saved card","marks":[{"type":"strong"}]}]},
          {"type":"table","attrs":{"isNumberColumnEnabled":false,"layout":"default"},"content":[
            {"type":"tableRow","content":[
              {"type":"tableHeader","attrs":{},"content":[{"type":"paragraph","content":[{"type":"text","text":"Card"}]}]},
              {"type":"tableHeader","attrs":{},"content":[{"type":"paragraph","content":[{"type":"text","text":"Fee"}]}]}]},
            {"type":"tableRow","content":[
              {"type":"tableCell","attrs":{},"content":[{"type":"paragraph","content":[{"type":"text","text":"Visa"}]}]},
              {"type":"tableCell","attrs":{},"content":[{"type":"paragraph","content":[{"type":"text","text":"1.4%"}]}]}]}]},
          {"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"m-shot","collection":"","alt":"screenshot.png"}}]},
          {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"asks for the CVC"}]}]}]}
        ]}
        """)!;
}
