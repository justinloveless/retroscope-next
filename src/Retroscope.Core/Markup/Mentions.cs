namespace Retroscope.Core.Markup;

public static class Mentions {
    // Distinct user ids in document order. Callers keep only current team members before writing `mentions`.
    public static IReadOnlyList<Guid> Extract(Doc doc) {
        return doc.Descendants().OfType<DocMention>().Select(mention => mention.UserId).Distinct().ToList();
    }
}
