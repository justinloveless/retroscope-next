namespace Retroscope.Core.Markup;

public enum MarkupDialect {
    // Retro cards, comments and action items: mentions and board images, no headings, rules or islands.
    Retro,

    // Jira comments and new issue descriptions: headings and rules, no mentions, images or islands.
    Jira,

    // Editing an existing Jira description: Jira plus top-level [[island:id]] lines (§f).
    JiraDescription,
}
