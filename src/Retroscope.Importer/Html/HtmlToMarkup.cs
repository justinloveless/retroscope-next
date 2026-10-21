using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Importer.Archive;

namespace Retroscope.Importer.Html;

/// <summary>What a conversion could not carry over; the caller adds each to the run report.</summary>
public enum HtmlLoss {
    /// <summary>A base64 image over <see cref="HtmlToMarkup.MaxImageBytes"/>; it became <c>[image removed]</c>.</summary>
    ImageOversize,

    /// <summary>A data URL that is not base64 PNG, JPEG or WebP by its magic bytes.</summary>
    ImageNotRecognised,

    /// <summary>A base64 image beyond the per-body image limit.</summary>
    ImageOverLimit,

    /// <summary>A base64 image in a body that has no board to own the blob (an action item whose board is gone).</summary>
    ImageWithoutBoard,

    /// <summary>An <c>img</c> whose source is neither a data URL nor an http(s) URL; it became <c>[image]</c>.</summary>
    ImageUnlinkable,

    /// <summary>A mention of someone who is not an imported user; it became <c>@name</c>.</summary>
    MentionOfNonUser,

    Truncated,

    Empty,
}

/// <param name="Field">Which body this is; decides the length cap.</param>
/// <param name="BoardId">The board that owns extracted images, or null when images cannot be kept.</param>
/// <param name="ResolveUser">Source user id → imported user id (following merges), or null.</param>
public sealed record HtmlContext(MarkupField Field, Guid? BoardId, Func<Guid, Guid?> ResolveUser);

/// <summary>A base64 image that the converted body references as <c>blob:&lt;BlobId&gt;</c>.</summary>
public sealed record ExtractedImage(Guid BlobId, Guid BoardId, string ContentType, byte[] Bytes, byte[] Sha256);

public sealed class HtmlConversion {
    public required string Markup { get; init; }

    public required Doc Doc { get; init; }

    /// <summary>Only images the final markup still references (truncation can drop some).</summary>
    public required IReadOnlyList<ExtractedImage> Images { get; init; }

    public required IReadOnlyDictionary<HtmlLoss, int> Losses { get; init; }

    /// <summary>Counts every loss as <c>html.&lt;table&gt;.&lt;loss&gt;</c>; truncated bodies are also listed for review.</summary>
    public void Report(ImportReport report, string sourceTable, string primaryKey) {
        foreach (var (loss, count) in Losses) {
            report.Count($"html.{sourceTable}.{CounterName(loss)}", count);
        }
        if (Losses.ContainsKey(HtmlLoss.Truncated)) {
            report.Note("truncatedBodies", new JsonObject { ["table"] = sourceTable, ["pk"] = primaryKey });
        }
    }

    private static string CounterName(HtmlLoss loss) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(loss.ToString());
}

/// <summary>
/// Stored HTML (TipTap output, older plain text) → canonical Retro Markup (DESIGN §h.4). Tags are treated exactly as
/// <see cref="Source.HtmlTagPolicy"/> says. The result is <c>Write(Parse(x))</c>-stable and passes
/// <see cref="MarkupLimits.Check"/> for its field: over-long bodies are truncated with a note, empty ones become
/// <c>(empty)</c>.
/// </summary>
public static class HtmlToMarkup {
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public const string TruncationNote = "…(truncated on import)";
    public const string EmptyBody = "(empty)";
    public const string RemovedImage = "[image removed]";

    /// <summary>Board image ids are <c>v5(blob:&lt;board&gt;:&lt;sha256&gt;)</c>, so re-runs agree and a card and its action item share one blob.</summary>
    public static readonly Guid BlobNamespace = new("120c1200-15f7-444a-8830-c25291d55aa1");

    public static Guid BlobId(Guid boardId, ReadOnlySpan<byte> sha256) =>
        UuidV5.Create(BlobNamespace, $"blob:{boardId:D}:{System.Convert.ToHexStringLower(sha256)}");

    public static HtmlConversion Convert(string? html, HtmlContext context) {
        var builder = new DocBuilder(context);
        var markup = Normalize(MarkupWriter.Write(builder.Build(html ?? "")));
        var losses = builder.Losses;

        var check = MarkupLimits.Check(markup, context.Field);
        if (check.Error == MarkupError.Empty) {
            markup = EmptyBody;
            Add(losses, HtmlLoss.Empty);
            check = MarkupLimits.Check(markup, context.Field);
        } else if (check.Error == MarkupError.TooLong) {
            (markup, check) = Truncate(markup, context.Field);
            Add(losses, HtmlLoss.Truncated);
        }
        var doc = check.Doc ?? throw new InvalidOperationException($"Converted markup failed its own check ({check.Error}).");

        var referenced = doc.Descendants().OfType<DocImage>().Select(x => x.BlobId).ToHashSet();
        return new HtmlConversion {
            Markup = markup,
            Doc = doc,
            Images = [.. builder.Images.Where(x => referenced.Contains(x.BlobId))],
            Losses = losses,
        };
    }

    internal static void Add(Dictionary<HtmlLoss, int> losses, HtmlLoss loss) => losses[loss] = losses.GetValueOrDefault(loss) + 1;

    internal static ExtractedImage Image(Guid boardId, string contentType, byte[] bytes) {
        var sha256 = SHA256.HashData(bytes);
        return new ExtractedImage(BlobId(boardId, sha256), boardId, contentType, bytes, sha256);
    }

    private static string Normalize(string markup) => MarkupWriter.Write(MarkupParser.Parse(markup, MarkupDialect.Retro));

    // Cutting canonical markup can split an escape or a token; re-normalising may then grow it slightly, so the
    // budget shrinks until the result fits.
    private static (string Markup, MarkupCheck Check) Truncate(string markup, MarkupField field) {
        var limit = MarkupLimits.MaxLength(field);
        var budget = limit - TruncationNote.Length - 2;
        while (true) {
            var candidate = Normalize(TakeCodePoints(markup, budget).TrimEnd() + "\n\n" + TruncationNote);
            var check = MarkupLimits.Check(candidate, field);
            if (check.IsValid) {
                return (candidate, check);
            }
            budget -= Math.Max(16, candidate.EnumerateRunes().Count() - limit);
        }
    }

    private static string TakeCodePoints(string text, int count) {
        var end = 0;
        for (var taken = 0; end < text.Length && taken < count; taken++) {
            end += char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
        }
        return text[..end];
    }
}
